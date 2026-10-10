using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// A folder Shelf watches: files dropped into it, by a downloader or over a network share, become books on their own.
// Each reader has a folder of their own inside it, named after them; files at the top go to the first admin. Each
// file there is a book, and a folder of tracks is one audiobook. Something is only taken once it has stopped
// changing, so a file still being copied is left alone. Afterwards it moves into .imported (or is deleted, when
// Import:AfterImport says so), and what Shelf cannot take moves into .not-added; the reader's files are never lost.
public sealed class FolderImport(IServiceScopeFactory scopes, BookImport import, IConfiguration configuration, ILogger<FolderImport> logger)
    : BackgroundService
{
    public const string Imported = ".imported";
    public const string NotAdded = ".not-added";

    // What each item looked like at the last scan: an item is taken when it looks the same twice running.
    private readonly Dictionary<string, (long Size, DateTime Changed)> seen = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim scanning = new(1, 1);

    public string? Folder => configuration["Import:Folder"] is { Length: > 0 } folder ? Path.GetFullPath(folder) : null;

    private bool Delete => string.Equals(configuration["Import:AfterImport"], "delete", StringComparison.OrdinalIgnoreCase);

    private TimeSpan Settle => TimeSpan.FromSeconds(configuration.GetValue("Import:SettleSeconds", 30));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Folder is null)
        {
            return;
        }

        logger.LogInformation("Watching {Folder} for books to add.", Folder);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, configuration.GetValue("Import:EverySeconds", 60))));
        do
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DbUpdateException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Could not look in the import folder; it will try again.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // One look at the folder. Returns how many items were taken.
    public async Task<int> ScanAsync(CancellationToken cancellationToken)
    {
        if (Folder is not { } root)
        {
            return 0;
        }

        await scanning.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(root);
            List<(int Id, string Name)> readers;
            int? admin;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
                readers = (await db.Readers.AsNoTracking().Select(reader => new { reader.Id, reader.Name }).ToListAsync(cancellationToken))
                    .Select(reader => (reader.Id, reader.Name)).ToList();
                admin = await db.Readers.AsNoTracking().Where(reader => reader.IsAdmin).OrderBy(reader => reader.Id).Select(reader => (int?)reader.Id).FirstOrDefaultAsync(cancellationToken);
            }

            var taken = 0;
            var current = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in Entries(root))
            {
                // A folder named after a reader is theirs; anything else at the top belongs to the first admin.
                var owner = Directory.Exists(entry) ? readers.FirstOrDefault(reader => Same(reader.Name, Path.GetFileName(entry))) : default;
                if (owner.Id != 0)
                {
                    foreach (var item in Entries(entry))
                    {
                        current.Add(item);
                        taken += await TryAsync(owner.Id, entry, item, cancellationToken) ? 1 : 0;
                    }
                }
                else if (admin is int adminId)
                {
                    current.Add(entry);
                    taken += await TryAsync(adminId, root, entry, cancellationToken) ? 1 : 0;
                }
            }

            foreach (var gone in seen.Keys.Where(key => !current.Contains(key)).ToList())
            {
                seen.Remove(gone);
            }

            return taken;
        }
        finally
        {
            scanning.Release();
        }
    }

    // The folder for each reader, made ready so a reader can see where to put their books.
    public IReadOnlyList<string> PrepareReaderFolders(IEnumerable<string> names)
    {
        if (Folder is not { } root)
        {
            return [];
        }

        var made = new List<string>();
        foreach (var name in names)
        {
            var safe = string.Concat(name.Where(character => !Path.GetInvalidFileNameChars().Contains(character))).Trim().TrimEnd('.');
            if (safe.Length > 0)
            {
                var path = Path.Combine(root, safe);
                Directory.CreateDirectory(path);
                made.Add(path);
            }
        }

        return made;
    }

    private async Task<bool> TryAsync(int readerId, string home, string item, CancellationToken cancellationToken)
    {
        var look = Look(item);
        if (look is null)
        {
            return false;
        }

        var steady = seen.TryGetValue(item, out var before) && before == look.Value && DateTime.UtcNow - look.Value.Changed >= Settle;
        seen[item] = look.Value;
        if (!steady)
        {
            return false;
        }

        seen.Remove(item);
        var files = Directory.Exists(item)
            ? Directory.EnumerateFiles(item, "*", SearchOption.AllDirectories).Where(path => !Hidden(path, item)).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [item];
        var streams = new List<FileStream>();
        List<ImportedFile> results;
        try
        {
            var batch = new List<ImportFile>();
            foreach (var path in files)
            {
                var stream = File.OpenRead(path);
                streams.Add(stream);
                batch.Add(new ImportFile(Path.GetFileName(path), stream, stream.Length));
            }

            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            results = Directory.Exists(item)
                ? await import.FolderAsync(db, batch, Path.GetFileName(item), keepBoth: false, cancellationToken)
                : await import.ImportAsync(db, batch, keepBoth: false, cancellationToken);
            var added = results.Where(result => result.Outcome is ImportOutcome.Added or ImportOutcome.AddedToExisting).ToList();
            if (added.Count > 0)
            {
                await Audit.NoteAsync(db, Say("Added from the import folder"), detail: string.Join(", ", added.Select(result => result.Title ?? result.FileName)), cancellationToken: cancellationToken);
            }
        }
        finally
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync();
            }
        }

        // Nothing it could take, and nothing it already had: set aside where the reader will see it.
        var refused = results.Count > 0 && results.All(result => result.Outcome is ImportOutcome.Unsupported or ImportOutcome.TooLarge or ImportOutcome.NeedsConverter);
        PutAway(item, home, refused);
        logger.LogInformation("Import folder: {Item} for reader {Reader}: {Outcomes}.", Path.GetFileName(item), readerId,
            string.Join(", ", results.Select(result => $"{result.FileName} {result.Outcome}")));
        return true;
    }

    private void PutAway(string item, string home, bool refused)
    {
        if (Delete && !refused)
        {
            if (Directory.Exists(item))
            {
                Directory.Delete(item, recursive: true);
            }
            else
            {
                File.Delete(item);
            }

            return;
        }

        var shelf = Path.Combine(home, refused ? NotAdded : Imported);
        Directory.CreateDirectory(shelf);
        var target = Path.Combine(shelf, Path.GetFileName(item));
        for (var n = 2; File.Exists(target) || Directory.Exists(target); n++)
        {
            target = Path.Combine(shelf, $"{Path.GetFileNameWithoutExtension(item)} ({n}){Path.GetExtension(item)}");
        }

        if (Directory.Exists(item))
        {
            Directory.Move(item, target);
        }
        else
        {
            File.Move(item, target);
        }
    }

    // An item's size, and the last time anything in it changed.
    private static (long Size, DateTime Changed)? Look(string item)
    {
        try
        {
            if (File.Exists(item))
            {
                var file = new FileInfo(item);
                return (file.Length, file.LastWriteTimeUtc);
            }

            if (Directory.Exists(item))
            {
                var files = new DirectoryInfo(item).EnumerateFiles("*", SearchOption.AllDirectories).Where(file => !Hidden(file.FullName, item)).ToList();
                return files.Count == 0 ? null : (files.Sum(file => file.Length), files.Max(file => file.LastWriteTimeUtc));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    // What is in a folder, leaving out Shelf's own folders and hidden files such as .DS_Store or a partial download.
    private static IEnumerable<string> Entries(string folder) =>
        Directory.EnumerateFileSystemEntries(folder)
            .Where(path => !Path.GetFileName(path).StartsWith('.') && !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase);

    private static bool Hidden(string path, string within) =>
        Path.GetRelativePath(within, path).Split(Path.DirectorySeparatorChar).Any(part => part.StartsWith('.'));

    private static bool Same(string reader, string folder) =>
        string.Equals(ReaderRules.Normalize(reader), ReaderRules.Normalize(folder), StringComparison.Ordinal);
}

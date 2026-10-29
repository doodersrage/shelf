using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// What a restore drill found in one backup: whether every entry reads back whole, the database inside opens and
// passes its own check, and how much it holds.
// MissingFiles: e-books, other formats, and recordings the database names that aren't in the zip.
public sealed record BackupVerdict(string Name, bool Ok, DateTimeOffset CheckedAt, int Entries, int Books, int Readers, bool HasFiles, string? Problem, int MissingFiles = 0);

public sealed record BackupWithVerdict(string Name, long Bytes, DateTimeOffset TakenAt, BackupVerdict? Verdict);

// A book in a backup that is not on the shelf now.
public sealed record BackupBook(int Id, string Title, string Author, DateTimeOffset AddedAt, bool HasFiles);

// Backups proved, not just taken; and one book brought back from one, without rolling back the shelf.
public sealed class BackupCheck(BackupSchedule schedule, EbookStore ebooks, AudioStore audio, CoverStore covers, ILogger<BackupCheck> logger)
{
    private const string VerdictSuffix = ".check.json";
    private static readonly SemaphoreSlim Opening = new(1, 1);

    private string OpenFolder => Path.Combine(schedule.Folder, ".open");

    public BackupVerdict? VerdictOf(string name) => schedule.PathOf(name) is { } path ? VerdictAt(path) : null;

    // The verdict kept beside a backup, if its drill has run.
    public static BackupVerdict? VerdictAt(string backupPath)
    {
        var path = backupPath + VerdictSuffix;
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<BackupVerdict>(File.ReadAllText(path), History.JsonOptions) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    public IReadOnlyList<BackupWithVerdict> List() =>
        [.. schedule.List().Select(backup => new BackupWithVerdict(backup.Name, backup.Bytes, backup.TakenAt, VerdictOf(backup.Name)))];

    // The newest backup a drill passed, if any.
    public BackupVerdict? LastGood() => List().Select(backup => backup.Verdict).FirstOrDefault(verdict => verdict is { Ok: true });

    // Reads every entry through, so a damaged zip shows itself; opens the database inside on its own, checks it, and
    // brings a copy up to this Shelf's schema, as a restore would have to.
    public async Task<BackupVerdict?> DrillAsync(string name, CancellationToken cancellationToken = default)
    {
        if (schedule.PathOf(name) is not { } path)
        {
            return null;
        }

        var verdict = await DrillAndKeepAsync(path, cancellationToken);
        if (!verdict.Ok)
        {
            logger.LogWarning("The backup {Name} did not pass its restore drill: {Problem}", name, verdict.Problem);
        }

        return verdict;
    }

    // The drill's verdict, kept beside the backup.
    public static async Task<BackupVerdict> DrillAndKeepAsync(string path, CancellationToken cancellationToken = default)
    {
        var verdict = await DrillPathAsync(path, cancellationToken);
        await File.WriteAllTextAsync(path + VerdictSuffix, JsonSerializer.Serialize(verdict, History.JsonOptions), cancellationToken);
        return verdict;
    }

    // The files the database names that the zip doesn't hold: every book's main e-book, its other formats, and its
    // recording. A backup with all its books but some of their files would restore without them.
    private static async Task<int> MissingFilesAsync(ShelfDb db, ZipArchive zip, CancellationToken cancellationToken)
    {
        var entries = zip.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
        var folders = zip.Entries.Where(entry => entry.FullName.StartsWith("audio/", StringComparison.Ordinal))
            .Select(entry => entry.FullName.Split('/') is { Length: > 2 } parts ? parts[1] : null).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var ebooks = await db.Books.IgnoreQueryFilters().Where(book => book.EbookStoredName != null).Select(book => book.EbookStoredName!).ToListAsync(cancellationToken);
        ebooks.AddRange(await db.EbookFormats.IgnoreQueryFilters().Select(format => format.StoredName).ToListAsync(cancellationToken));
        var recordings = await db.Books.IgnoreQueryFilters().Where(book => book.AudioStoredName != null).Select(book => book.AudioStoredName!).ToListAsync(cancellationToken);
        return ebooks.Count(stored => !entries.Contains("ebooks/" + stored.Replace('\\', '/')))
            + recordings.Count(stored => !folders.Contains(stored) && !entries.Contains("audio/" + stored));
    }

    public static async Task<BackupVerdict> DrillPathAsync(string path, CancellationToken cancellationToken = default)
    {
        var name = Path.GetFileName(path);
        var now = DateTimeOffset.UtcNow;
        BackupVerdict Fail(string problem, int entries = 0) => new(name, false, now, entries, 0, 0, false, problem);
        var temp = Path.Combine(Path.GetTempPath(), $"shelf-drill-{Guid.NewGuid():N}.db");
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var buffer = new byte[81920];
            foreach (var entry in zip.Entries)
            {
                // Reading an entry to its end checks it against the CRC the zip keeps for it.
                await using var stream = entry.Open();
                while (await stream.ReadAsync(buffer, cancellationToken) > 0)
                {
                }
            }

            var hasFiles = zip.Entries.Any(entry => entry.FullName.StartsWith("ebooks/", StringComparison.Ordinal) || entry.FullName.StartsWith("audio/", StringComparison.Ordinal));
            if (zip.GetEntry("shelf.db") is { } database)
            {
                database.ExtractToFile(temp);
                await using (var connection = new SqliteConnection($"Data Source={temp};Pooling=False"))
                {
                    await connection.OpenAsync(cancellationToken);
                    await using var check = connection.CreateCommand();
                    check.CommandText = "PRAGMA integrity_check";
                    if (await check.ExecuteScalarAsync(cancellationToken) is not "ok" and var answer)
                    {
                        return Fail(T("The database inside failed its own check: {0}", answer?.ToString() ?? ""), zip.Entries.Count);
                    }
                }

                await using var db = Open(temp, null);
                await db.Database.MigrateAsync(cancellationToken);
                var books = await db.Books.IgnoreQueryFilters().CountAsync(cancellationToken);
                var readers = await db.Readers.CountAsync(cancellationToken);
                return new BackupVerdict(name, true, now, zip.Entries.Count, books, readers, hasFiles, null, hasFiles ? await MissingFilesAsync(db, zip, cancellationToken) : 0);
            }

            // PostgreSQL without pg_dump: the files, and no database to bring the server back with.
            if (zip.GetEntry(Backup.NoDatabaseEntry) is not null)
            {
                return Fail(Backup.NoDatabaseProblem(), zip.Entries.Count);
            }

            // Taken on PostgreSQL without pg_dump by an earlier Shelf: the library of whoever took it as JSON, often
            // empty, never every reader's. It cannot restore the server, however many books it lists.
            if (zip.GetEntry("library.json") is not null)
            {
                return Fail(T("This backup holds a library as JSON in place of the database, with one reader's books at most, so it cannot restore the server. Install pg_dump so backups copy the PostgreSQL database."), zip.Entries.Count);
            }

            if (zip.GetEntry("shelf.sql") is { } dump)
            {
                // pg_dump ends a dump it finished with this line.
                using var reader = new StreamReader(dump.Open());
                var text = await reader.ReadToEndAsync(cancellationToken);
                return text.Contains("PostgreSQL database dump complete", StringComparison.Ordinal)
                    ? new BackupVerdict(name, true, now, zip.Entries.Count, 0, 0, hasFiles, null)
                    : Fail(T("The database dump inside stops before its end."), zip.Entries.Count);
            }

            return Fail(T("There is no database inside."), zip.Entries.Count);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or SqliteException or JsonException or InvalidOperationException)
        {
            return Fail(ex.Message);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static ShelfDb Open(string path, int? readerId)
    {
        var reader = new ShelfReader(new HttpContextAccessor());
        if (readerId is int id)
        {
            reader.Use(id);
        }

        var options = new DbContextOptionsBuilder<ShelfDb>().UseSqlite($"Data Source={path};Pooling=False").Options;
        return new ShelfDb(options, reader);
    }

    // The backup's database, unpacked once and brought up to date, kept while it is the one being looked in.
    private async Task<string?> OpenCopyAsync(string name, CancellationToken cancellationToken)
    {
        if (schedule.PathOf(name) is not { } path)
        {
            return null;
        }

        var copy = Path.Combine(OpenFolder, name + ".db");
        await Opening.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(copy))
            {
                return copy;
            }

            Directory.CreateDirectory(OpenFolder);
            foreach (var old in Directory.EnumerateFiles(OpenFolder))
            {
                File.Delete(old);
            }

            using (var zip = ZipFile.OpenRead(path))
            {
                if (zip.GetEntry("shelf.db") is not { } database)
                {
                    return null;
                }

                database.ExtractToFile(copy + ".partial");
            }

            await using (var db = Open(copy + ".partial", null))
            {
                await db.Database.MigrateAsync(cancellationToken);
            }

            SqliteConnection.ClearAllPools();
            File.Move(copy + ".partial", copy);
            return copy;
        }
        finally
        {
            Opening.Release();
        }
    }

    // The reader's books in a backup that are not on the shelf now: deleted since, or gone from the trash too.
    public async Task<BackupBook[]?> GoneBooksAsync(ShelfDb live, string name, string? search, CancellationToken cancellationToken = default)
    {
        if (await OpenCopyAsync(name, cancellationToken) is not { } copy)
        {
            return null;
        }

        await using var old = Open(copy, live.ReaderId);
        var query = old.Books.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(book => EF.Functions.Like(book.Title, pattern) || EF.Functions.Like(book.Author, pattern));
        }

        var books = await query.OrderBy(book => book.Title)
            .Select(book => new { book.Id, book.Title, book.Author, book.AddedAt, HasFiles = book.EbookStoredName != null || book.AudioStoredName != null })
            .ToListAsync(cancellationToken);
        var gone = new List<BackupBook>();
        foreach (var chunk in books.Chunk(1000))
        {
            var ids = chunk.Select(book => book.Id).ToList();
            var here = (await live.Books.Where(book => ids.Contains(book.Id)).Select(book => book.Id).ToListAsync(cancellationToken)).ToHashSet();
            gone.AddRange(chunk.Where(book => !here.Contains(book.Id)).Select(book => new BackupBook(book.Id, book.Title, book.Author, book.AddedAt, book.HasFiles)));
            if (gone.Count >= 500)
            {
                break;
            }
        }

        return [.. gone.Take(500)];
    }

    // One book, with all that was its, brought back from a backup as the trash would: its id kept when free, links to
    // what has gone since left out. A file it had comes back from the backup when the shelf no longer has it.
    public async Task<TrashRestore?> RestoreBookAsync(ShelfDb live, string name, int bookId, CancellationToken cancellationToken = default)
    {
        if (await live.Books.AnyAsync(book => book.Id == bookId, cancellationToken))
        {
            return new TrashRestore(false, bookId, T("That book is on your shelf already."));
        }

        if (await OpenCopyAsync(name, cancellationToken) is not { } copy || schedule.PathOf(name) is not { } zipPath)
        {
            return null;
        }

        (TrashPayload Payload, Book Book) captured;
        await using (var old = Open(copy, live.ReaderId))
        {
            var found = await Trash.CaptureBooksAsync(old, [bookId], cancellationToken);
            if (!found.TryGetValue(bookId, out captured))
            {
                return null;
            }
        }

        using var zip = ZipFile.OpenRead(zipPath);
        var result = await Trash.RestoreBookAsync(live, captured.Payload, live.ReaderId, book =>
        {
            book.EbookStoredName = Bring(zip, "ebooks", book.EbookStoredName, ebooks.OpenPath, ebooks.Root);
            if (book.EbookStoredName is null)
            {
                book.EbookFileName = null;
                book.EbookChapter = null;
                book.FileCover = false;
            }

            book.AudioStoredName = BringFolder(zip, book.AudioStoredName);
            if (book.AudioStoredName is null)
            {
                book.AudioFileName = null;
            }

            book.CoverImage = Bring(zip, "covers", book.CoverImage, covers.OpenPath, covers.Root);
            return book;
        }, cancellationToken);

        // Copy pictures came back as rows; any whose file is gone and not in the backup are let go.
        if (result is { Restored: true, BookId: int restored })
        {
            foreach (var photo in await live.CopyPhotos.Where(photo => photo.BookId == restored).ToListAsync(cancellationToken))
            {
                if (Bring(zip, "covers", photo.StoredName, covers.OpenPath, covers.Root) is null)
                {
                    live.CopyPhotos.Remove(photo);
                }
            }

            // So did its other formats: each file brought back from the backup, or the row let go.
            foreach (var format in await live.EbookFormats.Where(format => format.BookId == restored).ToListAsync(cancellationToken))
            {
                if (Bring(zip, "ebooks", format.StoredName, ebooks.OpenPath, ebooks.Root) is null)
                {
                    live.EbookFormats.Remove(format);
                }
            }

            await live.SaveChangesAsync(cancellationToken);
            await Audit.NoteAsync(live, Say("Brought a book back from a backup"), detail: captured.Book.Title, cancellationToken: cancellationToken);
        }

        return result;
    }

    // The stored file as the shelf has it, or else unpacked from the backup; null when neither has it.
    private static string? Bring(ZipArchive zip, string folder, string? storedName, Func<string?, string?> openPath, string root)
    {
        if (storedName is null || openPath(storedName) is not null)
        {
            return storedName;
        }

        if (Path.GetFileName(storedName) != storedName || zip.GetEntry($"{folder}/{storedName}") is not { } entry)
        {
            return null;
        }

        Directory.CreateDirectory(root);
        entry.ExtractToFile(Path.Combine(root, storedName));
        return storedName;
    }

    private string? BringFolder(ZipArchive zip, string? storedName)
    {
        if (storedName is null || audio.Tracks(storedName).Count > 0)
        {
            return storedName;
        }

        if (storedName.Length != 32 || !storedName.All(Uri.IsHexDigit))
        {
            return null;
        }

        var tracks = zip.Entries.Where(entry => entry.FullName.StartsWith($"audio/{storedName}/", StringComparison.Ordinal) && entry.Name.Length > 0).ToList();
        if (tracks.Count == 0)
        {
            return null;
        }

        var folder = Path.Combine(audio.Root, storedName);
        Directory.CreateDirectory(folder);
        foreach (var track in tracks)
        {
            track.ExtractToFile(Path.Combine(folder, track.Name));
        }

        return storedName;
    }

    public static void Map(RouteGroupBuilder books)
    {
        // Every reader looks only for their own books in a backup; the backups themselves stay the admin's.
        var backups = books.MapGroup("/backups").WithTags("Backups");
        backups.MapGet("/", (BackupCheck check) => TypedResults.Ok(check.List().Select(backup => new { backup.Name, backup.TakenAt, Ok = backup.Verdict?.Ok })));
        backups.MapGet("/{name}/books", async Task<IResult> (string name, string? q, ShelfDb db, BackupCheck check, CancellationToken cancellationToken) =>
            await check.GoneBooksAsync(db, name, q, cancellationToken) is { } gone ? TypedResults.Ok(gone) : TypedResults.NotFound());
        backups.MapPost("/{name}/books/{id:int}/restore", async Task<IResult> (string name, int id, ShelfDb db, BackupCheck check, CancellationToken cancellationToken) =>
            await check.RestoreBookAsync(db, name, id, cancellationToken) switch
            {
                null => TypedResults.NotFound(),
                { Restored: true } restored => TypedResults.Ok(restored),
                var problem => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Id"] = [problem.Problem ?? ""] }),
            });
    }
}

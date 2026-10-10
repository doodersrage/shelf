using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

// A reader's whole shelf in one zip: the JSON backup, plus every uploaded e-book and audiobook.
// A snapshot is the admin's copy of the server itself: the database and both file folders.
public static class Backup
{
    public const string ShelfEntry = "shelf.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    public static async Task<LibraryExport> ExportAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        (await ExportWithBooksAsync(db, cancellationToken)).Export;

    public static async Task WriteShelfAsync(
        ShelfDb db,
        EbookStore ebooks,
        AudioStore audio,
        string zipPath,
        CancellationToken cancellationToken = default)
    {
        var (export, books) = await ExportWithBooksAsync(db, cancellationToken);
        await using var output = File.Create(zipPath);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        var entry = zip.CreateEntry(ShelfEntry);
        await using (var stream = entry.Open())
        {
            await JsonSerializer.SerializeAsync(stream, export, JsonOptions, cancellationToken);
        }

        for (var index = 0; index < books.Count; index++)
        {
            var book = books[index];
            if (ebooks.OpenPath(book.EbookStoredName) is { } ebookPath && book.EbookFileName is { } fileName)
            {
                zip.CreateEntryFromFile(ebookPath, $"{Folder(index)}/ebook/{SafeName(fileName)}", CompressionLevel.NoCompression);
            }

            foreach (var track in audio.Tracks(book.AudioStoredName))
            {
                if (audio.TrackPath(book.AudioStoredName, track.Index) is { } trackPath)
                {
                    zip.CreateEntryFromFile(trackPath, $"{Folder(index)}/audio/{Path.GetFileName(trackPath)}", CompressionLevel.NoCompression);
                }
            }
        }
    }

    public static async Task<RestoreResult?> RestoreShelfAsync(
        ShelfDb db,
        EbookStore ebooks,
        AudioStore audio,
        string zipPath,
        CancellationToken cancellationToken = default)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var shelf = zip.GetEntry(ShelfEntry);
        if (shelf is null)
        {
            return null;
        }

        LibraryExport? export;
        try
        {
            await using var stream = shelf.Open();
            export = await JsonSerializer.DeserializeAsync<LibraryExport>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }

        if (export?.Books is null || export.YearlyGoal is < 0 or > 1000)
        {
            return null;
        }

        var added = new Book?[export.Books.Length];
        var result = await BookRules.ImportAsync(db, export, cancellationToken, added);
        var files = 0;
        for (var index = 0; index < added.Length; index++)
        {
            if (added[index] is not { } book)
            {
                continue;
            }

            var prefix = Folder(index) + "/";
            var ebook = zip.Entries.FirstOrDefault(item => item.FullName.StartsWith(prefix + "ebook/", StringComparison.Ordinal) && item.Name.Length > 0);
            if (ebook is not null)
            {
                await using var stream = ebook.Open();
                var saved = await ebooks.SaveAsync(stream, ebook.Name, cancellationToken);
                if (saved.Status == EbookSaveStatus.Saved)
                {
                    book.EbookStoredName = saved.StoredName;
                    book.EbookFileName = saved.FileName;
                    book.EbookChapter = 0;
                    files++;
                }
            }

            var tracks = zip.Entries
                .Where(item => item.FullName.StartsWith(prefix + "audio/", StringComparison.Ordinal) && item.Name.Length > 0)
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .ToList();
            if (tracks.Count > 0 && await RestoreAudioAsync(audio, book, tracks, cancellationToken))
            {
                files++;
            }
        }

        if (files > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return new RestoreResult(result.Added, result.Skipped, files);
    }

    // Everything on the server, for an admin to keep somewhere else. Restore by unpacking it over a stopped shelf.
    public static async Task WriteSnapshotAsync(
        ShelfDb db,
        EbookStore ebooks,
        AudioStore audio,
        string zipPath,
        CancellationToken cancellationToken = default,
        bool includeFiles = true)
    {
        var database = Path.Combine(Path.GetTempPath(), $"shelf-snapshot-{Guid.NewGuid():N}.db");
        try
        {
            // VACUUM INTO writes a consistent copy while the shelf keeps running.
            await db.Database.ExecuteSqlAsync($"VACUUM INTO {database}", cancellationToken);
            await using var output = File.Create(zipPath);
            using var zip = new ZipArchive(output, ZipArchiveMode.Create);
            zip.CreateEntryFromFile(database, "shelf.db");
            if (includeFiles)
            {
                AddFolder(zip, ebooks.Root, "ebooks");
                AddFolder(zip, audio.Root, "audio");
            }
        }
        finally
        {
            if (File.Exists(database))
            {
                File.Delete(database);
            }
        }
    }

    private static async Task<(LibraryExport Export, List<Book> Books)> ExportWithBooksAsync(ShelfDb db, CancellationToken cancellationToken)
    {
        var books = BookRules.Sort(await db.Books.AsNoTracking().WithDetails().ToListAsync(cancellationToken), "title");
        var export = new LibraryExport(
            await BookRules.GetGoalAsync(db, cancellationToken),
            books.Select(BookResponse.From).ToArray());
        return (export, books);
    }

    private static async Task<bool> RestoreAudioAsync(
        AudioStore audio,
        Book book,
        List<ZipArchiveEntry> tracks,
        CancellationToken cancellationToken)
    {
        if (tracks.Sum(track => track.Length) > AudioStore.MaxBytes)
        {
            return false;
        }

        // Each track is unpacked first, so the store reads one ordinary file at a time.
        var folder = Path.Combine(Path.GetTempPath(), $"shelf-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var uploads = new List<AudioUpload>();
        try
        {
            foreach (var track in tracks)
            {
                var path = Path.Combine(folder, SafeName(track.Name));
                await using (var input = track.Open())
                await using (var output = File.Create(path))
                {
                    // Never trust the size the zip claims: stop at the store's limit while copying.
                    var buffer = new byte[81920];
                    long written = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        written += read;
                        if (written > AudioStore.MaxBytes)
                        {
                            return false;
                        }

                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                }

                uploads.Add(new AudioUpload(track.Name, File.OpenRead(path), track.Length));
            }

            var saved = await audio.SaveAsync(uploads, cancellationToken);
            if (saved.Status != AudioSaveStatus.Saved)
            {
                return false;
            }

            book.AudioStoredName = saved.StoredName;
            book.AudioFileName = saved.FileName;
            book.AudioTrack = 0;
            book.AudioSeconds = 0;
            return true;
        }
        finally
        {
            foreach (var upload in uploads)
            {
                await upload.Content.DisposeAsync();
            }

            Directory.Delete(folder, recursive: true);
        }
    }

    private static void AddFolder(ZipArchive zip, string root, string name)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            zip.CreateEntryFromFile(file, $"{name}/{relative}", CompressionLevel.NoCompression);
        }
    }

    private static string Folder(int index) => $"books/{index + 1:0000}";

    private static string SafeName(string name)
    {
        var file = Path.GetFileName(name);
        foreach (var character in Path.GetInvalidFileNameChars())
        {
            file = file.Replace(character, '_');
        }

        return string.IsNullOrWhiteSpace(file) || file is "." or ".." ? "file" : file;
    }
}

public sealed record RestoreResult(int Added, int Skipped, int Files);

// Sends a file made for this one response, then deletes it.
public sealed class TempFileResult(string path, string contentType, string? downloadName = null) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        try
        {
            httpContext.Response.ContentType = contentType;
            httpContext.Response.ContentLength = new FileInfo(path).Length;
            if (downloadName is not null)
            {
                httpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"{downloadName}\"";
            }

            await using var input = File.OpenRead(path);
            await input.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    public static string NewPath(string extension) =>
        Path.Combine(Path.GetTempPath(), $"shelf-{Guid.NewGuid():N}{extension}");
}

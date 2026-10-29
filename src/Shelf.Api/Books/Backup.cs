using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

// A reader's whole shelf in one zip: the JSON backup, the same in plainer forms, plus every uploaded e-book and audiobook.
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
        CoverStore covers,
        string zipPath,
        CancellationToken cancellationToken = default)
    {
        var (export, books) = await ExportWithBooksAsync(db, cancellationToken);
        await using var output = File.Create(zipPath);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        var manifest = new ArchiveManifest();
        await using (var stream = manifest.Create(zip, ShelfEntry))
        {
            await JsonSerializer.SerializeAsync(stream, export, JsonOptions, cancellationToken);
        }

        await PortableExport.WriteAsync(db, zip, books, JsonOptions, manifest, cancellationToken);
        var formats = (await db.EbookFormats.AsNoTracking().ToListAsync(cancellationToken)).ToLookup(format => format.BookId);

        for (var index = 0; index < books.Count; index++)
        {
            var book = books[index];
            if (ebooks.OpenPath(book.EbookStoredName) is { } ebookPath && book.EbookFileName is { } fileName)
            {
                await manifest.CopyAsync(zip, ebookPath, $"{Folder(index)}/ebook/{SafeName(fileName)}", cancellationToken);
            }

            // Its other formats, each under its own format's name so two files called the same don't meet.
            foreach (var format in formats[book.Id])
            {
                if (ebooks.OpenPath(format.StoredName) is { } formatPath)
                {
                    var name = Path.GetFileNameWithoutExtension(SafeName(format.FileName)) + Path.GetExtension(format.StoredName).ToLowerInvariant();
                    await manifest.CopyAsync(zip, formatPath, $"{Folder(index)}/formats/{EbookStore.FormatOf(format.StoredName).ToLowerInvariant()}/{name}", cancellationToken);
                }
            }

            // A kept cover picture travels with its book; a cover address is in the catalog already.
            if (covers.OpenPath(book.CoverImage) is { } coverPath)
            {
                await manifest.CopyAsync(zip, coverPath, $"{Folder(index)}/cover{Path.GetExtension(coverPath)}", cancellationToken);
            }

            foreach (var track in audio.Tracks(book.AudioStoredName))
            {
                if (audio.TrackPath(book.AudioStoredName, track.Index) is { } trackPath)
                {
                    await manifest.CopyAsync(zip, trackPath, $"{Folder(index)}/audio/{Path.GetFileName(trackPath)}", cancellationToken);
                }
            }
        }

        await manifest.FinishAsync(zip, cancellationToken);
    }

    public static async Task<RestoreResult?> RestoreShelfAsync(
        ShelfDb db,
        EbookStore ebooks,
        AudioStore audio,
        CoverStore covers,
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
        var lost = 0;
        for (var index = 0; index < added.Length; index++)
        {
            if (added[index] is not { } book)
            {
                continue;
            }

            // One book's files failing (damaged in the download, or no room left) leaves the others: before, it ended
            // the restore with the books already saved, said "nothing was added", and lost the files already written.
            try
            {
                (var brought, var missed) = await RestoreFilesAsync(db, zip, Folder(index) + "/", book, ebooks, audio, covers, cancellationToken);
                files += brought;
                lost += missed;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                lost++;
            }
        }

        // Saved whatever came back, even if a book's later file failed after its first was written.
        await db.SaveChangesAsync(cancellationToken);

        // The download's collections and past loans, onto the books here they were of: those just brought back, and
        // those skipped as already on the shelf.
        var onShelf = await OnShelfAsync(db, export, cancellationToken);
        var collections = await RestoreCollectionsAsync(db, zip, onShelf, cancellationToken);
        var loans = await RestoreLoansAsync(db, zip, onShelf, cancellationToken);
        await RestorePlaylistsAsync(db, zip, onShelf, cancellationToken);

        // A download with a manifest is held up against it, and what doesn't match is said.
        var check = Preservation.Check(zipPath);
        return new RestoreResult(result.Added, result.Skipped, files, check.Damaged.Length + check.Missing.Length, collections, loans, lost);
    }

    // A book's e-book, cover, and recording from the download, and how many came back and how many could not.
    private static async Task<(int Brought, int Missed)> RestoreFilesAsync(ShelfDb db, ZipArchive zip, string prefix, Book book, EbookStore ebooks, AudioStore audio, CoverStore covers, CancellationToken cancellationToken)
    {
        var (brought, missed) = (0, 0);
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
                Covers.Note(book, ebooks);
                brought++;
            }
            else
            {
                missed++;
            }
        }

        // The other formats, kept as they came.
        foreach (var entry in zip.Entries.Where(item => item.FullName.StartsWith(prefix + "formats/", StringComparison.Ordinal) && item.Name.Length > 0))
        {
            await using var stream = entry.Open();
            var saved = await ebooks.SaveAsync(stream, entry.Name, cancellationToken, keepAsIs: true);
            if (saved is { Status: EbookSaveStatus.Saved, StoredName: { } storedName, FileName: { } fileName })
            {
                db.EbookFormats.Add(new EbookFormat { BookId = book.Id, StoredName = storedName, FileName = fileName, AddedAt = DateTimeOffset.UtcNow });
                brought++;
            }
            else
            {
                missed++;
            }
        }

        if (zip.Entries.FirstOrDefault(item => item.FullName.StartsWith(prefix + "cover.", StringComparison.Ordinal)) is { } cover)
        {
            await using var stream = cover.Open();
            if (await covers.SaveAsync(stream, cancellationToken) is { } kept)
            {
                book.CoverImage = kept;
                brought++;
            }
        }

        var tracks = zip.Entries
            .Where(item => item.FullName.StartsWith(prefix + "audio/", StringComparison.Ordinal) && item.Name.Length > 0)
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ToList();
        if (tracks.Count > 0)
        {
            if (await RestoreAudioAsync(audio, book, tracks, cancellationToken))
            {
                brought++;
            }
            else
            {
                missed++;
            }
        }

        return (brought, missed);
    }

    // For each book in the download, by its place there, the id of the book on this shelf it is now.
    private static async Task<int?[]> OnShelfAsync(ShelfDb db, LibraryExport export, CancellationToken cancellationToken)
    {
        var here = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var book in await db.Books.AsNoTracking().OrderBy(book => book.Id).Select(book => new { book.Id, book.Title, book.Author, book.Isbn }).ToListAsync(cancellationToken))
        {
            here.TryAdd(BookRules.ImportKey(book.Title, book.Author, book.Isbn), book.Id);
        }

        return [.. export.Books.Select(book => here.TryGetValue(BookRules.ImportKey(book.Title ?? "", book.Author ?? "", book.Isbn), out var id) ? id : (int?)null)];
    }

    private static async Task<T?> ReadEntryAsync<T>(ZipArchive zip, string name, CancellationToken cancellationToken)
    {
        if (zip.GetEntry(name) is not { } entry)
        {
            return default;
        }

        try
        {
            await using var stream = entry.Open();
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    // Each collection comes back by its name, made if there is none, with its books in order, their notes, and its
    // day to read by; one already here keeps what it has and gains what it lacks. How many came back or grew.
    // Playlists come back by name, with the books here they held, and the episodes of podcasts followed here.
    private static async Task RestorePlaylistsAsync(ShelfDb db, ZipArchive zip, int?[] onShelf, CancellationToken cancellationToken)
    {
        foreach (var kept in await ReadEntryAsync<PlaylistExport[]>(zip, "playlists.json", cancellationToken) ?? [])
        {
            if (string.IsNullOrWhiteSpace(kept.Name))
            {
                continue;
            }

            var name = kept.Name.Trim();
            var id = (await db.Playlists.ToListAsync(cancellationToken)).FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))?.Id
                ?? (await Playlists.CreateAsync(db, name, cancellationToken)).Made?.Id;
            if (id is not int playlist)
            {
                continue;
            }

            foreach (var entry in kept.Items ?? [])
            {
                if (entry.Book is int at && at >= 0 && at < onShelf.Length && onShelf[at] is int bookId)
                {
                    await Playlists.AddAsync(db, playlist, bookId, null, cancellationToken);
                }
                else if (entry.Feed is { } feed && entry.Episode is { } guid
                    && await db.PodcastEpisodes.Where(item => item.Guid == guid && item.Podcast!.FeedUrl == feed).Select(item => (int?)item.Id).FirstOrDefaultAsync(cancellationToken) is int episodeId)
                {
                    await Playlists.AddAsync(db, playlist, null, episodeId, cancellationToken);
                }
            }
        }
    }

    private static async Task<int> RestoreCollectionsAsync(ShelfDb db, ZipArchive zip, int?[] onShelf, CancellationToken cancellationToken)
    {
        var restored = 0;
        foreach (var kept in await ReadEntryAsync<CollectionExport[]>(zip, "collections.json", cancellationToken) ?? [])
        {
            if (string.IsNullOrWhiteSpace(kept.Name))
            {
                continue;
            }

            var name = kept.Name.Trim();
            var collection = (await db.Collections.ToListAsync(cancellationToken)).FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (collection is null)
            {
                var (made, problem) = await Collections.CreateAsync(db, name, kept.Description, cancellationToken);
                if (made is null || problem is not null)
                {
                    continue;
                }

                collection = made;
            }

            if (kept.ReadBy is not null && collection.ReadBy is null)
            {
                await Collections.PlanAsync(db, collection.Id, kept.ReadBy, cancellationToken);
            }

            var grew = false;
            foreach (var entry in kept.Books ?? [])
            {
                if (entry.Book < 0 || entry.Book >= onShelf.Length || onShelf[entry.Book] is not int bookId)
                {
                    continue;
                }

                var added = await Collections.AddAsync(db, collection.Id, bookId, cancellationToken);
                grew |= added == Collections.AddResult.Added;
                if (added is Collections.AddResult.Added && !string.IsNullOrWhiteSpace(entry.Note))
                {
                    await Collections.NoteAsync(db, collection.Id, bookId, entry.Note, cancellationToken);
                }
            }

            restored += grew ? 1 : 0;
        }

        return restored;
    }

    private sealed record LoanExport(int? Book, string? Borrower, DateOnly LentOn, DateOnly? DueOn, DateOnly? ReturnedOn, int Renewals,
        CopyCondition? ConditionOut, CopyCondition? ConditionBack, string? Note);

    // Past loans come back onto their books, each once. A loan still out is not: the book's own loan, in shelf.json,
    // brings that back as a loan here. How many came back.
    private static async Task<int> RestoreLoansAsync(ShelfDb db, ZipArchive zip, int?[] onShelf, CancellationToken cancellationToken)
    {
        var restored = 0;
        foreach (var loan in await ReadEntryAsync<LoanExport[]>(zip, "loans.json", cancellationToken) ?? [])
        {
            if (loan.ReturnedOn is null || loan.Book is not int at || at < 0 || at >= onShelf.Length || onShelf[at] is not int bookId
                || string.IsNullOrWhiteSpace(loan.Borrower))
            {
                continue;
            }

            var borrower = loan.Borrower.Trim() is { Length: > 120 } longName ? longName[..120] : loan.Borrower.Trim();
            if (await db.LoanRecords.AnyAsync(item => item.BookId == bookId && item.Borrower == borrower && item.LentOn == loan.LentOn, cancellationToken))
            {
                continue;
            }

            db.LoanRecords.Add(new LoanRecord
            {
                OwnerId = db.ReaderId, BookId = bookId, Borrower = borrower, LentOn = loan.LentOn, DueOn = loan.DueOn, ReturnedOn = loan.ReturnedOn,
                Renewals = Math.Max(0, loan.Renewals), ConditionOut = loan.ConditionOut, ConditionBack = loan.ConditionBack,
                Note = loan.Note is { Length: > LoanRecord.MaxNoteLength } note ? note[..LoanRecord.MaxNoteLength] : loan.Note,
            });
            restored++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return restored;
    }

    // In a snapshot holding no copy of the database, saying why: for its restore drill, and for whoever unpacks it.
    public const string NoDatabaseEntry = "NO-DATABASE.txt";

    // Everything on the server, for an admin to keep somewhere else. Restore by unpacking it over a stopped shelf.
    // False when the database could not be copied (PostgreSQL without pg_dump): the zip then holds the files and a
    // note saying so, never a stand-in that looks like a backup but cannot bring the server back.
    public static async Task<bool> WriteSnapshotAsync(
        ShelfDb db,
        EbookStore ebooks,
        AudioStore audio,
        CoverStore covers,
        string zipPath,
        CancellationToken cancellationToken = default,
        bool includeFiles = true,
        string? pgDump = null)
    {
        var database = Path.Combine(Path.GetTempPath(), $"shelf-snapshot-{Guid.NewGuid():N}.db");
        try
        {
            string entry;
            if (db.IsPostgres)
            {
                // PostgreSQL's own dump, by pg_dump on the path or at Database:PgDump. Without it there is no copy of the
                // database to keep: a reader's library as JSON would hold one reader's books at most, and no one else's.
                if (await PostgresDumpAsync(db, database, pgDump, cancellationToken))
                {
                    entry = "shelf.sql";
                }
                else
                {
                    entry = NoDatabaseEntry;
                    await File.WriteAllTextAsync(database, NoDatabaseProblem() + Environment.NewLine, cancellationToken);
                }
            }
            else
            {
                // VACUUM INTO writes a consistent copy while the shelf keeps running.
                await db.Database.ExecuteSqlAsync($"VACUUM INTO {database}", cancellationToken);
                entry = "shelf.db";
            }

            await using var output = File.Create(zipPath);
            using var zip = new ZipArchive(output, ZipArchiveMode.Create);
            zip.CreateEntryFromFile(database, entry);
            if (includeFiles)
            {
                // Kobo's copies and the copies with the book's details are made again as wanted.
                AddFolder(zip, ebooks.Root, "ebooks", "kepub", "with-details");
                AddFolder(zip, audio.Root, "audio");
            }

            // Cover pictures are small, and nothing else has them, so they always come along.
            AddFolder(zip, covers.Root, "covers");
            return entry != NoDatabaseEntry;
        }
        finally
        {
            if (File.Exists(database))
            {
                File.Delete(database);
            }
        }
    }

    // Why a snapshot on PostgreSQL holds no database, and what to do about it.
    public static string NoDatabaseProblem() =>
        Localization.Words.T("This backup holds no copy of the database: pg_dump, which copies a PostgreSQL database, was not found on the server or did not finish. Install pg_dump of the same version as the database server or newer, or set Database__PgDump to where it is, and back the database up with PostgreSQL's own tools as well.");

    private static async Task<bool> PostgresDumpAsync(ShelfDb db, string target, string? pgDump, CancellationToken cancellationToken)
    {
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
        string Quote(string? value) => "'" + (value ?? "").Replace("\\", "\\\\").Replace("'", "\\'") + "'";
        // The password goes to pg_dump in PGPASSWORD, never on its command line, which the machine's process list shows.
        var info = $"host={Quote(connection.Host)} port={connection.Port} dbname={Quote(connection.Database)} user={Quote(connection.Username)}";
        var environment = connection.Password is { Length: > 0 } password ? new Dictionary<string, string> { ["PGPASSWORD"] = password } : null;
        var tool = pgDump is { Length: > 0 } ? pgDump : Environment.GetEnvironmentVariable("Database__PgDump") ?? "pg_dump";
        return await Tools.RunAsync(tool, ["--dbname", info, "--no-owner", "--file", target], TimeSpan.FromMinutes(30), cancellationToken, environment) is not null && File.Exists(target);
    }

    private static async Task<(LibraryExport Export, List<Book> Books)> ExportWithBooksAsync(ShelfDb db, CancellationToken cancellationToken)
    {
        var books = BookRules.Sort(await db.Books.AsNoTracking().WithDetails().ToListAsync(cancellationToken), "title");
        var place = books.Select((book, index) => (book.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index);
        var reads = (await db.PastReads.AsNoTracking().ToListAsync(cancellationToken)).ToLookup(read => read.BookId);
        var journal = await db.JournalEntries.AsNoTracking().OrderBy(entry => entry.Date).ThenBy(entry => entry.Id).ToListAsync(cancellationToken);
        var links = await db.BookLinks.AsNoTracking().OrderBy(link => link.Id).ToListAsync(cancellationToken);
        var goals = await db.ReadingGoals.AsNoTracking().OrderBy(goal => goal.Year).ThenBy(goal => goal.Id).ToListAsync(cancellationToken);
        var fields = (await db.CustomFields.AsNoTracking().ToListAsync(cancellationToken)).OrderBy(field => field.Position).ThenBy(field => field.Id).ToList();
        var fieldNames = fields.ToDictionary(field => field.Id, field => field.Name);
        var values = await db.CustomValues.AsNoTracking().OrderBy(value => value.Id).ToListAsync(cancellationToken);
        var export = new LibraryExport(
            await BookRules.GetGoalAsync(db, cancellationToken),
            books.Select(book => BookResponse.From(book) is var response && reads[book.Id].Any()
                ? response with { PastReads = reads[book.Id].OrderBy(read => read.FinishedOn).Select(PastReadResponse.From).ToArray() }
                : response).ToArray(),
            journal.Select(entry => new JournalExport(entry.Date, entry.Text, entry.BookId is int id && place.TryGetValue(id, out var at) ? at : null)).ToArray(),
            links.Where(link => place.ContainsKey(link.FromBookId) && place.ContainsKey(link.ToBookId))
                .Select(link => new LinkExport(place[link.FromBookId], place[link.ToBookId], link.Kind, link.Note)).ToArray(),
            goals.Select(goal => new GoalExport(goal.Year, goal.Kind, goal.Name, goal.Target)).ToArray(),
            fields.Select(field => new FieldExport(field.Name, field.Kind)).ToArray(),
            values.Where(value => place.ContainsKey(value.BookId) && fieldNames.ContainsKey(value.FieldId))
                .Select(value => new FieldValueExport(place[value.BookId], fieldNames[value.FieldId], value.Value)).ToArray());
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

    private static void AddFolder(ZipArchive zip, string root, string name, params string[] skipped)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (skipped.Any(folder => relative.StartsWith(folder + "/", StringComparison.Ordinal)))
            {
                continue;
            }

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

public sealed record RestoreResult(int Added, int Skipped, int Files, int Damaged = 0, int Collections = 0, int Loans = 0, int Lost = 0);

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
                // A name in any language: plain letters for old clients, and the name itself as filename*.
                var plain = new string(downloadName.Select(letter => letter is >= ' ' and <= '~' and not '"' and not '\\' ? letter : '_').ToArray());
                httpContext.Response.Headers.ContentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = plain, FileNameStar = downloadName }.ToString();
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

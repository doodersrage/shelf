using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Localization;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public sealed record CalibreBook(
    int Id,
    string Title,
    string Author,
    string Folder,
    bool HasCover,
    string? Series,
    double SeriesIndex,
    string? Publisher,
    int? Rating,
    string? Comments,
    string? Isbn,
    string? Language,
    int? Year,
    IReadOnlyList<string> Tags,
    IReadOnlyList<(string Format, string Name)> Files,
    IReadOnlyList<CalibreValue>? Custom = null);

// What a book holds in one of Calibre's custom columns, as the field it becomes here: its name, its kind, and the
// value written as a reader would type it.
public sealed record CalibreValue(string Field, FieldKind Kind, string Value);

public sealed class CalibreProblem(string message) : Exception(message);

// A Calibre library, read straight from its metadata.db and the folder of each book beside it. Read only: nothing
// in the library is changed.
public static partial class CalibreLibrary
{
    // The file Shelf takes from each book: an e-book it reads as it is, then one it converts, then an audiobook.
    private static readonly string[] Preferred = ["EPUB", "PDF", "CBZ", "AZW3", "MOBI", "AZW", "M4B", "MP3", "M4A"];

    public static string Database(string folder) => Path.Combine(folder, "metadata.db");

    public static List<CalibreBook> Read(string folder)
    {
        var database = Database(folder);
        if (!File.Exists(database))
        {
            throw new CalibreProblem(T("There is no Calibre library at {0}: it has no metadata.db.", folder));
        }

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            var tags = Pairs(connection, "SELECT l.book, t.name FROM books_tags_link l JOIN tags t ON t.id = l.tag");
            var custom = CustomColumns(connection);
            var files = new Dictionary<int, List<(string, string)>>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT book, format, name FROM data";
                using var rows = command.ExecuteReader();
                while (rows.Read())
                {
                    files.TryAdd(rows.GetInt32(0), []);
                    files[rows.GetInt32(0)].Add((rows.GetString(1).ToUpperInvariant(), rows.GetString(2)));
                }
            }

            var books = new List<CalibreBook>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT b.id, b.title, b.path, b.has_cover, b.series_index, b.pubdate,
                        (SELECT group_concat(name, ' & ') FROM (SELECT a.name FROM books_authors_link l JOIN authors a ON a.id = l.author WHERE l.book = b.id ORDER BY l.id)),
                        (SELECT s.name FROM books_series_link l JOIN series s ON s.id = l.series WHERE l.book = b.id),
                        (SELECT p.name FROM books_publishers_link l JOIN publishers p ON p.id = l.publisher WHERE l.book = b.id),
                        (SELECT r.rating FROM books_ratings_link l JOIN ratings r ON r.id = l.rating WHERE l.book = b.id),
                        (SELECT c.text FROM comments c WHERE c.book = b.id),
                        (SELECT i.val FROM identifiers i WHERE i.book = b.id AND i.type = 'isbn'),
                        (SELECT g.lang_code FROM books_languages_link l JOIN languages g ON g.id = l.lang_code WHERE l.book = b.id ORDER BY l.item_order LIMIT 1)
                    FROM books b
                    ORDER BY b.author_sort, b.sort
                    """;
                using var rows = command.ExecuteReader();
                while (rows.Read())
                {
                    var id = rows.GetInt32(0);
                    var rating = rows.IsDBNull(9) ? (int?)null : rows.GetInt32(9);
                    books.Add(new CalibreBook(
                        id,
                        rows.GetString(1).Trim(),
                        rows.IsDBNull(6) ? BookImport.UnknownAuthor : rows.GetString(6).Trim(),
                        rows.GetString(2),
                        !rows.IsDBNull(3) && rows.GetBoolean(3),
                        rows.IsDBNull(7) ? null : rows.GetString(7),
                        rows.IsDBNull(4) ? 1 : rows.GetDouble(4),
                        rows.IsDBNull(8) ? null : rows.GetString(8),
                        rating is > 0 ? Math.Clamp((rating.Value + 1) / 2, 1, 5) : null,
                        rows.IsDBNull(10) ? null : Text(rows.GetString(10)),
                        rows.IsDBNull(11) ? null : rows.GetString(11),
                        rows.IsDBNull(12) ? null : TwoLetters(rows.GetString(12)),
                        Year(rows.IsDBNull(5) ? null : rows.GetString(5)),
                        tags.TryGetValue(id, out var named) ? named : [],
                        files.TryGetValue(id, out var kept) ? kept : [],
                        custom.TryGetValue(id, out var values) ? values : null));
                }
            }

            return books;
        }
        catch (SqliteException ex)
        {
            throw new CalibreProblem(T("That metadata.db could not be read as a Calibre library: {0}", ex.Message));
        }
    }

    // A book's custom columns, into the reader's fields of the same names, made if they have none (up to the most a
    // reader keeps). A field they have already keeps its kind, and takes only the values that fit it; a value the
    // book holds already is never replaced.
    public static async Task FillFieldsAsync(ShelfDb db, int bookId, IReadOnlyList<CalibreValue> values, CancellationToken cancellationToken = default)
    {
        if (values.Count == 0)
        {
            return;
        }

        var fields = await db.CustomFields.ToListAsync(cancellationToken);
        var held = (await db.CustomValues.Where(value => value.BookId == bookId).Select(value => value.FieldId).ToListAsync(cancellationToken)).ToHashSet();
        foreach (var value in values)
        {
            var field = fields.FirstOrDefault(item => string.Equals(item.Name, value.Field, StringComparison.OrdinalIgnoreCase));
            if (field is null)
            {
                if (fields.Count >= CustomFields.MaxFields)
                {
                    continue;
                }

                field = new CustomField { OwnerId = db.ReaderId, Name = value.Field, Kind = value.Kind, Position = fields.Count };
                db.CustomFields.Add(field);
                await db.SaveChangesAsync(cancellationToken);
                fields.Add(field);
            }

            if (held.Contains(field.Id) || CustomFields.Normalize(field.Kind, value.Value).Value is not { } kept)
            {
                continue;
            }

            db.CustomValues.Add(new CustomValue { BookId = bookId, FieldId = field.Id, Value = kept });
            held.Add(field.Id);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    // The first kind Shelf takes that Calibre lists for the book but its folder doesn't hold; null when none is missing.
    public static string? Missing(string library, CalibreBook book) =>
        Preferred.FirstOrDefault(format => book.Files.FirstOrDefault(file => file.Format == format) is { Name: not null } file
            && !File.Exists(Path.Combine(library, book.Folder, $"{file.Name}.{format.ToLowerInvariant()}")));

    // The file to bring for a book, as a full path, with its kind; null when none of its files is one Shelf takes.
    public static (string Path, string Format)? Best(string library, CalibreBook book)
    {
        foreach (var format in Preferred)
        {
            if (book.Files.FirstOrDefault(file => file.Format == format) is { Name: not null } file)
            {
                var path = Path.Combine(library, book.Folder, $"{file.Name}.{format.ToLowerInvariant()}");
                if (File.Exists(path))
                {
                    return (path, format);
                }
            }
        }

        return null;
    }

    // Calibre's custom columns, by book: numbers as numbers, yes/no, dates, a rating as stars, and anything else (text,
    // tags, series, comments, a choice) as text, several values joined. A library without them, or one this can't
    // read, simply brings none.
    private static Dictionary<int, List<CalibreValue>> CustomColumns(SqliteConnection connection)
    {
        var found = new Dictionary<int, List<CalibreValue>>();
        var columns = new List<(int Id, string Name, string Type, bool Normalized)>();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, name, datatype, normalized FROM custom_columns WHERE mark_for_delete = 0 ORDER BY id";
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                columns.Add((rows.GetInt32(0), rows.GetString(1).Trim(), rows.GetString(2), rows.GetBoolean(3)));
            }
        }
        catch (SqliteException)
        {
            return found;
        }

        foreach (var (id, name, type, normalized) in columns.Where(column => column.Name.Length is > 0 and <= CustomField.MaxNameLength).Take(CustomFields.MaxFields))
        {
            var kind = type switch
            {
                "int" or "float" or "rating" => FieldKind.Number,
                "bool" => FieldKind.YesNo,
                "datetime" => FieldKind.Date,
                _ => FieldKind.Text,
            };
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = normalized
                    ? $"SELECT l.book, c.value FROM books_custom_column_{id}_link l JOIN custom_column_{id} c ON c.id = l.value ORDER BY l.id"
                    : $"SELECT book, value FROM custom_column_{id}";
                using var rows = command.ExecuteReader();
                var byBook = new Dictionary<int, List<string>>();
                while (rows.Read())
                {
                    if (rows.IsDBNull(1))
                    {
                        continue;
                    }

                    var raw = rows.GetValue(1);
                    var text = type switch
                    {
                        "bool" => Convert.ToInt64(raw, CultureInfo.InvariantCulture) != 0 ? "yes" : "no",
                        "rating" => (Convert.ToDouble(raw, CultureInfo.InvariantCulture) / 2).ToString(CultureInfo.InvariantCulture),
                        "datetime" => Year(raw.ToString()) is null ? null : raw.ToString()![..Math.Min(10, raw.ToString()!.Length)],
                        "int" or "float" => Convert.ToString(raw, CultureInfo.InvariantCulture),
                        "comments" => Text(raw.ToString() ?? ""),
                        _ => raw.ToString(),
                    };
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        byBook.TryAdd(rows.GetInt32(0), []);
                        byBook[rows.GetInt32(0)].Add(text.Trim());
                    }
                }

                foreach (var (book, values) in byBook)
                {
                    var value = string.Join(", ", values.Distinct());
                    found.TryAdd(book, []);
                    found[book].Add(new CalibreValue(name, kind, value.Length > CustomValue.MaxLength ? value[..CustomValue.MaxLength] : value));
                }
            }
            catch (SqliteException)
            {
                // A column this can't read is left out; the rest still come.
            }
        }

        return found;
    }

    private static Dictionary<int, List<string>> Pairs(SqliteConnection connection, string sql)
    {
        var found = new Dictionary<int, List<string>>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            found.TryAdd(rows.GetInt32(0), []);
            found[rows.GetInt32(0)].Add(rows.GetString(1));
        }

        return found;
    }

    // Calibre's dates are ISO text; an unknown one is the year 101.
    private static int? Year(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= 1000 and <= 2100 ? year : null;

    // Calibre keeps ISO 639-2 codes such as eng; the rest of Shelf speaks the two-letter kind.
    private static string? TwoLetters(string code) =>
        CultureInfo.GetCultures(CultureTypes.NeutralCultures).FirstOrDefault(culture => culture.ThreeLetterISOLanguageName == code)?.TwoLetterISOLanguageName ?? code;

    // A description in HTML, as plain paragraphs.
    private static string? Text(string html)
    {
        var broken = Breaks().Replace(html, "\n");
        var plain = WebUtility.HtmlDecode(Tags().Replace(broken, ""));
        var paragraphs = plain.Split('\n').Select(line => Spaces().Replace(line, " ").Trim()).Where(line => line.Length > 0);
        var text = string.Join("\n\n", paragraphs);
        return text.Length == 0 ? null : text.Length > 4000 ? text[..3997].TrimEnd() + "…" : text;
    }

    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/h[1-6])\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Breaks();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

public enum CalibreState
{
    Waiting,
    Done,
    Skipped,
    Failed,
}

public sealed record CalibreLine(int Id, string Title, CalibreState State, int? BookId, string? Note);

public sealed class CalibreJob(Guid id, int readerId, string library, IReadOnlyList<CalibreBook> books) : IShelfJob
{
    public string Language { get; } = Words.Current;

    public Guid Id { get; } = id;
    public int ReaderId { get; } = readerId;
    public string Library { get; } = library;
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<CalibreBook> Books { get; } = books;
    public ConcurrentDictionary<int, CalibreLine> Entries { get; } = new(books.Select(book => KeyValuePair.Create(book.Id, new CalibreLine(book.Id, book.Title, CalibreState.Waiting, null, null))));
    public bool Finished { get; set; }
    public bool Stopping { get; private set; }

    // The book being brought now.
    public int? Current { get; set; }

    public string Title(bool finished) => finished ? T("Brought across from {0}", "Calibre") : T("Bringing books from {0}", "Calibre");

    public string Page => "/import/calibre";

    public void Stop() => Stopping = true;

    public IReadOnlyList<ImportLine> Lines() => [.. Books.Select(book => Entries[book.Id]).Select(line => new ImportLine(line.Title, line.State switch
    {
        CalibreState.Waiting when Current == line.Id => ImportLineState.Working,
        CalibreState.Waiting => ImportLineState.Waiting,
        CalibreState.Done => ImportLineState.Done,
        CalibreState.Skipped => ImportLineState.Skipped,
        _ => ImportLineState.Failed,
    }, line.Note, line.BookId, 0))];
}

// Imports run in the background, a book at a time: its file, cover, series, tags, rating, and description. A book
// with no file Shelf takes still comes in as a catalog entry.
public sealed class CalibreImporter(IServiceScopeFactory scopes, ShelfJobs register, ILogger<CalibreImporter> logger) : BackgroundService
{
    private readonly Channel<Guid> queue = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, CalibreJob> jobs = new();

    public CalibreJob Start(int readerId, string library, IReadOnlyList<CalibreBook> books)
    {
        var job = new CalibreJob(Guid.NewGuid(), readerId, library, books);
        jobs[job.Id] = job;
        register.Add(job);
        queue.Writer.TryWrite(job.Id);
        return job;
    }

    public CalibreJob? Latest(int readerId) =>
        jobs.Values.Where(job => job.ReaderId == readerId).OrderByDescending(job => job.Started).FirstOrDefault();

    // The whole job, for a test or a caller that waits.
    public async Task RunAsync(CalibreJob job, CancellationToken cancellationToken)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(job.Language);
        foreach (var book in job.Books)
        {
            if (job.Stopping)
            {
                job.Entries[book.Id] = job.Entries[book.Id] with { State = CalibreState.Skipped, Note = T("Stopped before this one.") };
                continue;
            }

            job.Current = book.Id;
            try
            {
                job.Entries[book.Id] = await ImportAsync(job, book, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or DbUpdateException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                logger.LogInformation(ex, "Could not import {Title} from Calibre.", book.Title);
                job.Entries[book.Id] = job.Entries[book.Id] with { State = CalibreState.Failed, Note = T("Shelf could not read its files.") };
            }
        }

        job.Current = null;
        job.Finished = true;
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(job.ReaderId);
        var done = job.Entries.Values.Count(line => line.State == CalibreState.Done);
        await Audit.NoteAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), Say("Brought books from Calibre"), detail: Counts.Books(done), cancellationToken: cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (jobs.TryGetValue(id, out var job))
            {
                try
                {
                    await RunAsync(job, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // The job ends rather than showing as running for good, and the next one in the queue still runs.
                    logger.LogError(ex, "A Calibre import stopped part way.");
                    job.Finished = true;
                }
            }
        }
    }

    private async Task<CalibreLine> ImportAsync(CalibreJob job, CalibreBook book, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(job.ReaderId);
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var import = scope.ServiceProvider.GetRequiredService<BookImport>();
        var covers = scope.ServiceProvider.GetRequiredService<CoverStore>();
        var line = job.Entries[book.Id];
        var known = new FileDetails(book.Title, book.Author, book.Isbn, book.Publisher, book.Year, book.Language);

        ImportedFile? result = null;
        string? missingNote = null;
        if (CalibreLibrary.Best(job.Library, book) is { } file)
        {
            await using var stream = File.OpenRead(file.Path);
            var upload = new ImportFile(Path.GetFileName(file.Path), stream, stream.Length);
            result = BookImport.IsAudio(file.Path)
                ? await import.AudioAsync(db, [upload], upload.Name, known, keepBoth: false, cancellationToken)
                : await import.EbookAsync(db, upload, keepBoth: false, known, cancellationToken);
            if (result.Outcome == ImportOutcome.AlreadyOnShelf)
            {
                return line with { State = CalibreState.Skipped, BookId = result.SameAsId, Note = T("Already on your shelf.") };
            }

            if (result.Outcome is not (ImportOutcome.Added or ImportOutcome.AddedToExisting))
            {
                return line with
                {
                    State = CalibreState.Failed,
                    Note = result.Outcome switch
                    {
                        ImportOutcome.NeedsConverter => T("A Kindle file needs Calibre's converter on the server."),
                        ImportOutcome.Damaged => T("Its {0} file looks damaged or cut short.", file.Format),
                        ImportOutcome.TooLarge => T("Its {0} file is larger than the shelf takes.", file.Format),
                        ImportOutcome.Full => T("There's no room left in your storage for it."),
                        _ => T("Shelf could not read its files."),
                    },
                };
            }
        }
        else if (CalibreLibrary.Missing(job.Library, book) is { } missing)
        {
            // Calibre lists a file Shelf takes, and its folder doesn't hold it: said, and the book comes as an entry.
            missingNote = T("Its {0} file is missing from the Calibre folder, so it came without a file.", missing);
        }

        var bookId = result?.BookId ?? await import.CatalogAsync(db, known, cancellationToken);
        if (bookId is null)
        {
            return line with { State = CalibreState.Skipped, Note = T("Already on your shelf.") };
        }

        var entry = await db.Books.Include(item => item.Tags).FirstAsync(item => item.Id == bookId, cancellationToken);
        if (entry.Series is null && book.Series is { Length: > 0 } series)
        {
            entry.Series = series.Length > 200 ? series[..200] : series;
            entry.SeriesNumber = book.SeriesIndex == Math.Floor(book.SeriesIndex) && book.SeriesIndex is >= 0 and <= 10000 ? (int)book.SeriesIndex : null;
        }

        entry.Rating ??= book.Rating;
        entry.Notes ??= book.Comments;
        var tags = book.Tags.Select(tag => tag.Trim().ToLowerInvariant()).Where(tag => tag.Length is > 0 and <= BookRules.MaxTagLength);
        await BookRules.SyncTagsAsync(db, entry, [.. entry.Tags.Select(tag => tag.Name).Concat(tags).Distinct().Take(BookRules.MaxTags)], cancellationToken);
        await CalibreLibrary.FillFieldsAsync(db, entry.Id, book.Custom ?? [], cancellationToken);
        if (!Covers.HasCover(entry) && book.HasCover && Path.Combine(job.Library, book.Folder, "cover.jpg") is var cover && File.Exists(cover)
            && new FileInfo(cover).Length <= CoverStore.MaxBytes)
        {
            entry.CoverImage = await covers.SaveAsync(await File.ReadAllBytesAsync(cover, cancellationToken), cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return line with { State = CalibreState.Done, BookId = entry.Id, Note = result is null ? missingNote ?? T("No file Shelf takes; added to the catalog.") : null };
    }
}

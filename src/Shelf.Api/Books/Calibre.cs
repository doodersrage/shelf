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
    IReadOnlyList<(string Format, string Name)> Files);

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
                        files.TryGetValue(id, out var kept) ? kept : []));
                }
            }

            return books;
        }
        catch (SqliteException ex)
        {
            throw new CalibreProblem(T("That metadata.db could not be read as a Calibre library: {0}", ex.Message));
        }
    }

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

public sealed class CalibreJob(Guid id, int readerId, string library, IReadOnlyList<CalibreBook> books)
{
    public string Language { get; } = Words.Current;

    public Guid Id { get; } = id;
    public int ReaderId { get; } = readerId;
    public string Library { get; } = library;
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<CalibreBook> Books { get; } = books;
    public ConcurrentDictionary<int, CalibreLine> Lines { get; } = new(books.Select(book => KeyValuePair.Create(book.Id, new CalibreLine(book.Id, book.Title, CalibreState.Waiting, null, null))));
    public bool Finished { get; set; }
}

// Imports run in the background, a book at a time: its file, cover, series, tags, rating, and description. A book
// with no file Shelf takes still comes in as a catalog entry.
public sealed class CalibreImporter(IServiceScopeFactory scopes, ILogger<CalibreImporter> logger) : BackgroundService
{
    private readonly Channel<Guid> queue = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, CalibreJob> jobs = new();

    public CalibreJob Start(int readerId, string library, IReadOnlyList<CalibreBook> books)
    {
        var job = new CalibreJob(Guid.NewGuid(), readerId, library, books);
        jobs[job.Id] = job;
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
            try
            {
                job.Lines[book.Id] = await ImportAsync(job, book, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or DbUpdateException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                logger.LogInformation(ex, "Could not import {Title} from Calibre.", book.Title);
                job.Lines[book.Id] = job.Lines[book.Id] with { State = CalibreState.Failed, Note = T("Shelf could not read its files.") };
            }
        }

        job.Finished = true;
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(job.ReaderId);
        var done = job.Lines.Values.Count(line => line.State == CalibreState.Done);
        await Audit.NoteAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), Say("Brought books from Calibre"), detail: Counts.Books(done), cancellationToken: cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (jobs.TryGetValue(id, out var job))
            {
                await RunAsync(job, stoppingToken);
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
        var line = job.Lines[book.Id];
        var known = new FileDetails(book.Title, book.Author, book.Isbn, book.Publisher, book.Year, book.Language);

        ImportedFile? result = null;
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
                    Note = result.Outcome == ImportOutcome.NeedsConverter ? T("A Kindle file needs Calibre's converter on the server.") : T("Shelf could not read its files."),
                };
            }
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
        if (!Covers.HasCover(entry) && book.HasCover && Path.Combine(job.Library, book.Folder, "cover.jpg") is var cover && File.Exists(cover)
            && new FileInfo(cover).Length <= CoverStore.MaxBytes)
        {
            entry.CoverImage = await covers.SaveAsync(await File.ReadAllBytesAsync(cover, cancellationToken), cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return line with { State = CalibreState.Done, BookId = entry.Id, Note = result is null ? T("No file Shelf takes; added to the catalog.") : null };
    }
}

using System.ComponentModel.DataAnnotations;
using Shelf.Api.Readers;

namespace Shelf.Api.Books;

public enum BookStatus
{
    Want,
    Reading,
    Finished,
    Abandoned,
}

public enum BookFormat
{
    Hardcover,
    Paperback,
    Ebook,
    Audiobook,
}

public enum Acquisition
{
    Bought,
    Gift,
    Found,
}

public enum CopyCondition
{
    Fine,
    Good,
    Fair,
    Poor,
}

public sealed class Book
{
    public int Id { get; set; }
    public int? OwnerId { get; set; }
    public Reader? Owner { get; set; }
    public required string Title { get; set; }
    public required string Author { get; set; }
    public BookStatus Status { get; set; }
    public int? Rating { get; set; }
    public int? Year { get; set; }
    public string? Isbn { get; set; }
    public int? Pages { get; set; }
    public int? CurrentPage { get; set; }
    public string? Notes { get; set; }
    public DateOnly? StartedOn { get; set; }
    public DateOnly? FinishedOn { get; set; }
    public string? LoanedTo { get; set; }
    public DateOnly? LoanedOn { get; set; }
    public DateOnly? DueOn { get; set; }
    public int? BorrowerId { get; set; }
    public Reader? Borrower { get; set; }
    public string? Subtitle { get; set; }
    public string? Publisher { get; set; }
    public string? Language { get; set; }
    public BookFormat? Format { get; set; }
    public string? Series { get; set; }
    public int? SeriesNumber { get; set; }
    public string? CoverUrl { get; set; }

    // The e-book file carries a cover picture of its own, served at /books/{id}/cover.
    public bool FileCover { get; set; }

    // A cover picture kept on the shelf (see CoverStore): uploaded, from an audiobook's art, or from a catalog.
    public string? CoverImage { get; set; }

    // How KOReader names this e-book when it syncs a place: an MD5 of samples through the file.
    public string? KoreaderDigest { get; set; }

    // The cover to show: the address given for it, or else the e-book's own.
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string? CoverShown => Covers.For(Id, CoverUrl, FileCover, CoverImage);

    public string? Review { get; set; }
    public bool Loved { get; set; }
    public bool Queued { get; set; }
    public string? Location { get; set; }
    public DateOnly? AcquiredOn { get; set; }
    public Acquisition? Acquisition { get; set; }
    public CopyCondition? Condition { get; set; }
    public string? EbookFileName { get; set; }
    public string? EbookStoredName { get; set; }
    public int? EbookChapter { get; set; }
    public string? AudioFileName { get; set; }
    public string? AudioStoredName { get; set; }
    public int? AudioTrack { get; set; }
    public int? AudioSeconds { get; set; }
    public string? Translator { get; set; }

    // Who reads the audiobook aloud.
    public string? Narrator { get; set; }
    public string? OriginalTitle { get; set; }
    public string? Inscription { get; set; }
    public string? RecommendedBy { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public List<Tag> Tags { get; set; } = [];
    public List<Quote> Quotes { get; set; } = [];
    public List<Highlight> Highlights { get; set; } = [];
    public List<ReadingSession> Sessions { get; set; } = [];
}

public sealed class ReadingSession
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public Book? Book { get; set; }
    public DateOnly Date { get; set; }
    public int? FromPage { get; set; }
    public int? ToPage { get; set; }
    public string? Note { get; set; }
}

// A library view a reader keeps: its filters and order as the library's own address, under a name of their choosing.
public sealed class SavedSearch
{
    public const int MaxNameLength = 60;
    public const int MaxQueryLength = 2000;

    public int Id { get; set; }
    public int OwnerId { get; set; }
    public required string Name { get; set; }
    public required string Query { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // Set while the search is shared by link: the link's random part.
    public string? ShareToken { get; set; }
}

public sealed class ShelfSetting
{
    public int Id { get; set; }
    public int YearlyGoal { get; set; }
    public string? SyncAddress { get; set; }
    public string? SyncKey { get; set; }
    public int? ReaderTextSize { get; set; }
    public int? ReaderLineHeight { get; set; }
    public int? ReaderWidth { get; set; }
    public bool LibraryAsList { get; set; }
    public int? AudioSpeed { get; set; }

    // Series alerts: asked for, and when Open Library was last asked.
    public bool WatchSeries { get; set; }
    public DateTimeOffset? SeriesCheckedAt { get; set; }
}

// How a reader likes an e-book set: text size in percent, line height in hundredths, and line length in ems.
public sealed record ReaderType(int Size = ReaderType.DefaultSize, int Leading = ReaderType.DefaultLeading, int Width = ReaderType.DefaultWidth)
{
    public const int DefaultSize = 100;
    public const int DefaultLeading = 160;
    public const int DefaultWidth = 38;

    public ReaderType Clamped() => new(Math.Clamp(Size, 80, 200), Math.Clamp(Leading, 120, 220), Math.Clamp(Width, 24, 80));

    public string Css()
    {
        var type = Clamped();
        var leading = (type.Leading / 100.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return $$"""
            <style id="shelf-type">
            html { font-size: {{type.Size}}%; }
            body { max-width: {{type.Width}}em; margin: 0 auto; padding: 1.5rem 1.25rem 3rem; }
            body, p, li, dd, blockquote { line-height: {{leading}} !important; }
            img, svg { max-width: 100%; height: auto; }
            @media (prefers-color-scheme: dark) {
              html { background: #202824; color: #f1ede4; }
              a { color: #9fc0ad; }
            }
            </style>
            """;
    }
}

public sealed class Tag
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public List<Book> Books { get; set; } = [];
}

public sealed class Quote
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public Book? Book { get; set; }
    public required string Text { get; set; }
    public int? Page { get; set; }
    public DateTimeOffset NotedAt { get; set; }
}

public sealed class Highlight
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public Book? Book { get; set; }

    // Null for the owner's marks; a borrower's marks carry their reader id and stay theirs.
    public int? ReaderId { get; set; }
    public int ChapterIndex { get; set; }
    public required string Text { get; set; }
    public string? Note { get; set; }
    public string? Prefix { get; set; }
    public string? Suffix { get; set; }
    public DateTimeOffset NotedAt { get; set; }
}

public sealed record CreateBookRequest(
    [Required, MaxLength(200)] string Title,
    [Required, MaxLength(200)] string Author,
    BookStatus Status,
    [Range(1, 5)] int? Rating,
    [Range(1000, 2100)] int? Year = null,
    [MaxLength(32)] string? Isbn = null,
    [Range(1, 20000)] int? Pages = null,
    [Range(0, 20000)] int? CurrentPage = null,
    [MaxLength(4000)] string? Notes = null,
    DateOnly? StartedOn = null,
    DateOnly? FinishedOn = null,
    [MaxLength(120)] string? LoanedTo = null,
    string[]? Tags = null,
    [MaxLength(200)] string? Subtitle = null,
    [MaxLength(200)] string? Publisher = null,
    [MaxLength(40)] string? Language = null,
    BookFormat? Format = null,
    [MaxLength(200)] string? Series = null,
    [Range(1, 999)] int? SeriesNumber = null,
    [MaxLength(500)] string? CoverUrl = null,
    [MaxLength(4000)] string? Review = null,
    bool Loved = false,
    DateOnly? LoanedOn = null,
    [MaxLength(80)] string? Location = null,
    DateOnly? AcquiredOn = null,
    [MaxLength(200)] string? Translator = null,
    [MaxLength(200)] string? OriginalTitle = null,
    [MaxLength(120)] string? RecommendedBy = null,
    [MaxLength(500)] string? Inscription = null,
    DateOnly? DueOn = null,
    bool Queued = false,
    Acquisition? Acquisition = null,
    CopyCondition? Condition = null,
    [MaxLength(200)] string? Narrator = null)
{
    public BookWrite ToWrite() => BookWrite.From(
        Title, Author, Status, Rating, Year, Isbn, Pages, CurrentPage, Notes, StartedOn, FinishedOn, LoanedTo, Tags,
        Subtitle, Publisher, Language, Format, Series, SeriesNumber, CoverUrl, Review, Loved, LoanedOn,
        Location, AcquiredOn, Translator, RecommendedBy, OriginalTitle, Inscription, DueOn, Queued, Acquisition, Condition, Narrator);
}

public sealed record UpdateBookRequest(
    [Required, MaxLength(200)] string Title,
    [Required, MaxLength(200)] string Author,
    BookStatus Status,
    [Range(1, 5)] int? Rating,
    [Range(1000, 2100)] int? Year = null,
    [MaxLength(32)] string? Isbn = null,
    [Range(1, 20000)] int? Pages = null,
    [Range(0, 20000)] int? CurrentPage = null,
    [MaxLength(4000)] string? Notes = null,
    DateOnly? StartedOn = null,
    DateOnly? FinishedOn = null,
    [MaxLength(120)] string? LoanedTo = null,
    string[]? Tags = null,
    [MaxLength(200)] string? Subtitle = null,
    [MaxLength(200)] string? Publisher = null,
    [MaxLength(40)] string? Language = null,
    BookFormat? Format = null,
    [MaxLength(200)] string? Series = null,
    [Range(1, 999)] int? SeriesNumber = null,
    [MaxLength(500)] string? CoverUrl = null,
    [MaxLength(4000)] string? Review = null,
    bool Loved = false,
    DateOnly? LoanedOn = null,
    [MaxLength(80)] string? Location = null,
    DateOnly? AcquiredOn = null,
    [MaxLength(200)] string? Translator = null,
    [MaxLength(200)] string? OriginalTitle = null,
    [MaxLength(120)] string? RecommendedBy = null,
    [MaxLength(500)] string? Inscription = null,
    DateOnly? DueOn = null,
    bool Queued = false,
    Acquisition? Acquisition = null,
    CopyCondition? Condition = null,
    [MaxLength(200)] string? Narrator = null)
{
    public BookWrite ToWrite() => BookWrite.From(
        Title, Author, Status, Rating, Year, Isbn, Pages, CurrentPage, Notes, StartedOn, FinishedOn, LoanedTo, Tags,
        Subtitle, Publisher, Language, Format, Series, SeriesNumber, CoverUrl, Review, Loved, LoanedOn,
        Location, AcquiredOn, Translator, RecommendedBy, OriginalTitle, Inscription, DueOn, Queued, Acquisition, Condition, Narrator);
}

public sealed record BookWrite(
    string Title,
    string Author,
    BookStatus Status,
    int? Rating,
    int? Year,
    string? Isbn,
    int? Pages,
    int? CurrentPage,
    string? Notes,
    DateOnly? StartedOn,
    DateOnly? FinishedOn,
    string? LoanedTo,
    IReadOnlyList<string> Tags,
    string? Subtitle,
    string? Publisher,
    string? Language,
    BookFormat? Format,
    string? Series,
    int? SeriesNumber,
    string? CoverUrl,
    string? Review,
    bool Loved,
    DateOnly? LoanedOn,
    string? Location,
    DateOnly? AcquiredOn,
    string? Translator,
    string? RecommendedBy,
    string? OriginalTitle,
    string? Inscription,
    DateOnly? DueOn,
    bool Queued,
    Acquisition? Acquisition,
    CopyCondition? Condition,
    string? Narrator = null)
{
    public static BookWrite From(
        string title,
        string author,
        BookStatus status,
        int? rating,
        int? year,
        string? isbn,
        int? pages,
        int? currentPage,
        string? notes,
        DateOnly? startedOn,
        DateOnly? finishedOn,
        string? loanedTo,
        IReadOnlyList<string>? tags,
        string? subtitle,
        string? publisher,
        string? language,
        BookFormat? format,
        string? series,
        int? seriesNumber,
        string? coverUrl,
        string? review,
        bool loved,
        DateOnly? loanedOn,
        string? location,
        DateOnly? acquiredOn,
        string? translator,
        string? recommendedBy,
        string? originalTitle,
        string? inscription,
        DateOnly? dueOn,
        bool queued,
        Acquisition? acquisition,
        CopyCondition? condition,
        string? narrator = null) => new(
            title, author, status, rating, year, isbn, pages, currentPage, notes, startedOn, finishedOn, loanedTo, tags ?? [],
            subtitle, publisher, language, format, series, seriesNumber, coverUrl, review, loved, loanedOn,
            location, acquiredOn, translator, recommendedBy, originalTitle, inscription, dueOn, queued, acquisition, condition, narrator);

    public static BookWrite From(BookResponse book) => From(
        book.Title, book.Author, book.Status, book.Rating, book.Year, book.Isbn, book.Pages, book.CurrentPage, book.Notes,
        book.StartedOn, book.FinishedOn, book.LoanedTo, book.Tags, book.Subtitle, book.Publisher, book.Language, book.Format,
        book.Series, book.SeriesNumber, book.CoverUrl, book.Review, book.Loved, book.LoanedOn,
        book.Location, book.AcquiredOn, book.Translator, book.RecommendedBy, book.OriginalTitle, book.Inscription, book.DueOn, book.Queued, book.Acquisition, book.Condition, book.Narrator);
}

public sealed record BookResponse(
    int Id,
    string Title,
    string Author,
    BookStatus Status,
    int? Rating,
    int? Year,
    string? Isbn,
    int? Pages,
    int? CurrentPage,
    string? Notes,
    DateOnly? StartedOn,
    DateOnly? FinishedOn,
    string? LoanedTo,
    DateTimeOffset AddedAt,
    string[] Tags,
    QuoteResponse[] Quotes,
    string? Subtitle,
    string? Publisher,
    string? Language,
    BookFormat? Format,
    string? Series,
    int? SeriesNumber,
    string? CoverUrl,
    string? Review,
    bool Loved,
    DateOnly? LoanedOn,
    ReadingSessionResponse[] Sessions,
    string? Location,
    DateOnly? AcquiredOn,
    string? Translator,
    string? RecommendedBy,
    string? OriginalTitle,
    string? Inscription,
    DateOnly? DueOn,
    bool Queued,
    Acquisition? Acquisition,
    CopyCondition? Condition,
    string? EbookFileName,
    string? AudioFileName,
    int? BorrowerId = null,
    HighlightResponse[]? Highlights = null,
    string? Narrator = null)
{
    public static BookResponse From(Book book) => new(
        book.Id,
        book.Title,
        book.Author,
        book.Status,
        book.Rating,
        book.Year,
        book.Isbn,
        book.Pages,
        book.CurrentPage,
        book.Notes,
        book.StartedOn,
        book.FinishedOn,
        book.LoanedTo,
        book.AddedAt,
        book.Tags.Select(tag => tag.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
        book.Quotes.OrderBy(quote => quote.NotedAt).ThenBy(quote => quote.Id).Select(QuoteResponse.From).ToArray(),
        book.Subtitle,
        book.Publisher,
        book.Language,
        book.Format,
        book.Series,
        book.SeriesNumber,
        book.CoverUrl,
        book.Review,
        book.Loved,
        book.LoanedOn,
        book.Sessions.OrderBy(session => session.Date).ThenBy(session => session.Id).Select(ReadingSessionResponse.From).ToArray(),
        book.Location,
        book.AcquiredOn,
        book.Translator,
        book.RecommendedBy,
        book.OriginalTitle,
        book.Inscription,
        book.DueOn,
        book.Queued,
        book.Acquisition,
        book.Condition,
        book.EbookFileName,
        book.AudioFileName,
        book.BorrowerId,
        book.Highlights.OrderBy(mark => mark.ChapterIndex).ThenBy(mark => mark.Id).Select(HighlightResponse.From).ToArray(),
        book.Narrator);
}

public sealed record ShelfCopy(int Id, string Title, Acquisition? Acquisition);

public sealed record ConditionGroup(CopyCondition Condition, ShelfCopy[] Books);

public sealed record CreateHighlightRequest(
    [Required, MaxLength(1000)] string Text,
    int ChapterIndex,
    [MaxLength(2000)] string? Note = null,
    [MaxLength(80)] string? Prefix = null,
    [MaxLength(80)] string? Suffix = null);

public sealed record UpdateHighlightRequest([MaxLength(2000)] string? Note);

public sealed record HighlightResponse(
    int Id,
    int ChapterIndex,
    string Text,
    string? Note,
    string? Prefix,
    string? Suffix,
    DateTimeOffset NotedAt)
{
    public static HighlightResponse From(Highlight highlight) => new(
        highlight.Id,
        highlight.ChapterIndex,
        highlight.Text,
        highlight.Note,
        highlight.Prefix,
        highlight.Suffix,
        highlight.NotedAt);
}

// A moment in an audiobook to come back to, with an optional note. Like a highlight, it is the owner's when
// ReaderId is null, and a borrower's own otherwise.
public sealed class AudioBookmark
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public Book? Book { get; set; }
    public int? ReaderId { get; set; }
    public int Track { get; set; }
    public int Seconds { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// Where a borrower stopped in a lent book, kept apart from the owner's place.
public sealed class LoanPlace
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public int ReaderId { get; set; }
    public int? EbookChapter { get; set; }
    public int? AudioTrack { get; set; }
    public int? AudioSeconds { get; set; }
}

// Another reader asking the owner to lend them a book from an open shelf.
public sealed class LoanAskRow
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public int ReaderId { get; set; }
    public DateTimeOffset AskedAt { get; set; }
}

public sealed record AuthorCount(string Name, int Count);

public sealed record PlaceCount(string Name, int Count);

public sealed record RecommenderCount(string Name, int Count);

public sealed record LoanCount(string Name, int Count, int Overdue = 0);

public sealed record SeriesBook(int Id, string Title, int? Number, BookStatus Status);

public sealed record FinishedBook(int Id, string Title, DateOnly FinishedOn);

public sealed record YearBook(int Id, string Title, DateOnly? FinishedOn);

public sealed record FinishedYear(int? Year, YearBook[] Books);

public sealed record RecentSession(int BookId, string Title, DateOnly Date, int? FromPage, int? ToPage);

public sealed record ReadingMonth(int Year, int Month, int[] Days);

public sealed record DayReading(int BookId, string Title, int? FromPage, int? ToPage);

public sealed record SeriesShelf(string Name, SeriesBook[] Books);

public sealed record QuoteResponse(int Id, int BookId, string Text, int? Page, DateTimeOffset NotedAt)
{
    public static QuoteResponse From(Quote quote) => new(quote.Id, quote.BookId, quote.Text, quote.Page, quote.NotedAt);
}

public sealed record CreateQuoteRequest(
    [Required, MaxLength(1000)] string Text,
    [Range(1, 20000)] int? Page);

public sealed record QuoteListItem(
    int Id,
    int BookId,
    string Title,
    string Author,
    string Text,
    int? Page,
    DateTimeOffset NotedAt);

public sealed record ReadingSessionResponse(int Id, int BookId, DateOnly Date, int? FromPage, int? ToPage, string? Note)
{
    public static ReadingSessionResponse From(ReadingSession session) =>
        new(session.Id, session.BookId, session.Date, session.FromPage, session.ToPage, session.Note);
}

public sealed record CreateSessionRequest(
    DateOnly? Date,
    [Range(0, 20000)] int? FromPage,
    [Range(0, 20000)] int? ToPage,
    [MaxLength(500)] string? Note);

public sealed record TagCountResponse(string Name, int Count);

public sealed record ShelfStatsResponse(
    int Total,
    int Want,
    int Reading,
    int Finished,
    int Abandoned,
    double? AverageRating,
    int PagesRead,
    int FinishedThisYear,
    TagCountResponse[] Tags,
    int Loved,
    int OnLoan,
    int YearlyGoal);

public sealed record ShelfSettingsResponse(int YearlyGoal);

public sealed record UpdateSettingsRequest([Range(0, 1000)] int YearlyGoal);

public sealed record LibraryExport(int YearlyGoal, BookResponse[] Books);

public sealed record ImportResult(int Added, int Skipped);

public sealed record CatalogMatch(
    string Title,
    string Author,
    int? Year,
    int? Pages,
    string? Publisher,
    string? Language,
    string? CoverUrl,
    string? Isbn = null,
    string? Subtitle = null,
    BookFormat? Format = null,
    string[]? Tags = null);

public sealed record EditionChoice(
    string? Title,
    string? Publisher,
    int? Year,
    int? Pages,
    string? Language,
    string? PhysicalFormat,
    int? CoverId,
    string? Subtitle,
    string? Isbn);

using System.ComponentModel.DataAnnotations;

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

public sealed class Book
{
    public int Id { get; set; }
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
    public string? Subtitle { get; set; }
    public string? Publisher { get; set; }
    public string? Language { get; set; }
    public BookFormat? Format { get; set; }
    public string? Series { get; set; }
    public int? SeriesNumber { get; set; }
    public string? CoverUrl { get; set; }
    public string? Review { get; set; }
    public bool Loved { get; set; }
    public string? Location { get; set; }
    public DateOnly? AcquiredOn { get; set; }
    public string? Translator { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public List<Tag> Tags { get; set; } = [];
    public List<Quote> Quotes { get; set; } = [];
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

public sealed class ShelfSetting
{
    public int Id { get; set; }
    public int YearlyGoal { get; set; }
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
    [MaxLength(200)] string? Translator = null)
{
    public BookWrite ToWrite() => BookWrite.From(
        Title, Author, Status, Rating, Year, Isbn, Pages, CurrentPage, Notes, StartedOn, FinishedOn, LoanedTo, Tags,
        Subtitle, Publisher, Language, Format, Series, SeriesNumber, CoverUrl, Review, Loved, LoanedOn,
        Location, AcquiredOn, Translator);
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
    [MaxLength(200)] string? Translator = null)
{
    public BookWrite ToWrite() => BookWrite.From(
        Title, Author, Status, Rating, Year, Isbn, Pages, CurrentPage, Notes, StartedOn, FinishedOn, LoanedTo, Tags,
        Subtitle, Publisher, Language, Format, Series, SeriesNumber, CoverUrl, Review, Loved, LoanedOn,
        Location, AcquiredOn, Translator);
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
    string? Translator)
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
        string? translator) => new(
            title, author, status, rating, year, isbn, pages, currentPage, notes, startedOn, finishedOn, loanedTo, tags ?? [],
            subtitle, publisher, language, format, series, seriesNumber, coverUrl, review, loved, loanedOn,
            location, acquiredOn, translator);

    public static BookWrite From(BookResponse book) => From(
        book.Title, book.Author, book.Status, book.Rating, book.Year, book.Isbn, book.Pages, book.CurrentPage, book.Notes,
        book.StartedOn, book.FinishedOn, book.LoanedTo, book.Tags, book.Subtitle, book.Publisher, book.Language, book.Format,
        book.Series, book.SeriesNumber, book.CoverUrl, book.Review, book.Loved, book.LoanedOn,
        book.Location, book.AcquiredOn, book.Translator);
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
    string? Translator)
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
        book.Translator);
}

public sealed record AuthorCount(string Name, int Count);

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
    string? CoverUrl);

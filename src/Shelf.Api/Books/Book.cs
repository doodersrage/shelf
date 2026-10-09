using System.ComponentModel.DataAnnotations;

namespace Shelf.Api.Books;

public enum BookStatus
{
    Want,
    Reading,
    Finished,
    Abandoned,
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
    public DateTimeOffset AddedAt { get; set; }
    public List<Tag> Tags { get; set; } = [];
    public List<Quote> Quotes { get; set; } = [];
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
    string[]? Tags = null)
{
    public BookWrite ToWrite() => new(
        Title, Author, Status, Rating, Year, Isbn, Pages, CurrentPage, Notes, StartedOn, FinishedOn, LoanedTo, Tags ?? []);
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
    string[]? Tags = null)
{
    public BookWrite ToWrite() => new(
        Title, Author, Status, Rating, Year, Isbn, Pages, CurrentPage, Notes, StartedOn, FinishedOn, LoanedTo, Tags ?? []);
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
    IReadOnlyList<string> Tags);

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
    QuoteResponse[] Quotes)
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
        book.Quotes.OrderBy(quote => quote.NotedAt).ThenBy(quote => quote.Id).Select(QuoteResponse.From).ToArray());
}

public sealed record QuoteResponse(int Id, int BookId, string Text, int? Page, DateTimeOffset NotedAt)
{
    public static QuoteResponse From(Quote quote) => new(quote.Id, quote.BookId, quote.Text, quote.Page, quote.NotedAt);
}

public sealed record CreateQuoteRequest(
    [Required, MaxLength(1000)] string Text,
    [Range(1, 20000)] int? Page);

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
    TagCountResponse[] Tags);

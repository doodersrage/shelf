using System.ComponentModel.DataAnnotations;

namespace Shelf.Api.Books;

public enum BookStatus
{
    Want,
    Reading,
    Finished,
}

public sealed class Book
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public required string Author { get; set; }
    public BookStatus Status { get; set; }
    public int? Rating { get; set; }
}

public sealed record CreateBookRequest(
    [Required, MaxLength(200)] string Title,
    [Required, MaxLength(200)] string Author,
    BookStatus Status,
    [Range(1, 5)] int? Rating);

public sealed record UpdateBookRequest(
    [Required, MaxLength(200)] string Title,
    [Required, MaxLength(200)] string Author,
    BookStatus Status,
    [Range(1, 5)] int? Rating);

public sealed record BookResponse(int Id, string Title, string Author, BookStatus Status, int? Rating)
{
    public static BookResponse From(Book book) =>
        new(book.Id, book.Title, book.Author, book.Status, book.Rating);
}

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

public static class BookEndpoints
{
    public static RouteGroupBuilder MapBooks(this IEndpointRouteBuilder app)
    {
        var books = app.MapGroup("/books").WithTags("Books");

        books.MapGet("/", ListBooks);
        books.MapGet("/{id:int}", GetBook);
        books.MapPost("/", CreateBook);
        books.MapPut("/{id:int}", UpdateBook);
        books.MapDelete("/{id:int}", DeleteBook);

        return books;
    }

    private static async Task<Ok<BookResponse[]>> ListBooks(ShelfDb db, CancellationToken cancellationToken)
    {
        var books = await db.Books
            .AsNoTracking()
            .OrderBy(book => book.Title)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(books.Select(BookResponse.From).ToArray());
    }

    private static async Task<Results<Ok<BookResponse>, NotFound>> GetBook(
        int id,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        var book = await db.Books.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        return book is null ? TypedResults.NotFound() : TypedResults.Ok(BookResponse.From(book));
    }

    private static async Task<Created<BookResponse>> CreateBook(
        CreateBookRequest request,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        var book = new Book
        {
            Title = request.Title.Trim(),
            Author = request.Author.Trim(),
            Status = request.Status,
            Rating = request.Rating,
            Year = request.Year,
        };

        db.Books.Add(book);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/books/{book.Id}", BookResponse.From(book));
    }

    private static async Task<Results<Ok<BookResponse>, NotFound>> UpdateBook(
        int id,
        UpdateBookRequest request,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        var book = await db.Books.FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (book is null)
        {
            return TypedResults.NotFound();
        }

        book.Title = request.Title.Trim();
        book.Author = request.Author.Trim();
        book.Status = request.Status;
        book.Rating = request.Rating;
        book.Year = request.Year;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(BookResponse.From(book));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteBook(
        int id,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        var book = await db.Books.FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (book is null)
        {
            return TypedResults.NotFound();
        }

        db.Books.Remove(book);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}

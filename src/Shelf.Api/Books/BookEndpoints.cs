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
        books.MapGet("/stats", GetStats).WithTags("Shelf");
        books.MapGet("/{id:int}", GetBook);
        books.MapPost("/", CreateBook);
        books.MapPut("/{id:int}", UpdateBook);
        books.MapDelete("/{id:int}", DeleteBook);
        books.MapGet("/{id:int}/quotes", ListQuotes);
        books.MapPost("/{id:int}/quotes", CreateQuote);
        books.MapDelete("/{id:int}/quotes/{quoteId:int}", DeleteQuote);

        return books;
    }

    private static async Task<Ok<BookResponse[]>> ListBooks(
        ShelfDb db,
        CancellationToken cancellationToken,
        string? q = null,
        BookStatus? status = null,
        string? tag = null,
        string? sort = null)
    {
        var books = await BookRules.Filtered(db.Books, q, status, tag)
            .AsNoTracking()
            .WithDetails()
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(BookRules.Sort(books, sort).Select(BookResponse.From).ToArray());
    }

    private static async Task<Ok<ShelfStatsResponse>> GetStats(ShelfDb db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await BookRules.SummarizeAsync(db, cancellationToken));

    private static async Task<Results<Ok<BookResponse>, NotFound>> GetBook(
        int id,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        var book = await db.Books.AsNoTracking().WithDetails().FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        return book is null ? TypedResults.NotFound() : TypedResults.Ok(BookResponse.From(book));
    }

    private static async Task<Results<Created<BookResponse>, ValidationProblem>> CreateBook(
        CreateBookRequest request,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        var write = request.ToWrite();
        var problems = BookRules.Validate(write);
        if (problems is not null)
        {
            return TypedResults.ValidationProblem(problems);
        }

        var book = new Book
        {
            Title = write.Title.Trim(),
            Author = write.Author.Trim(),
        };
        BookRules.Apply(book, write);
        db.Books.Add(book);
        await BookRules.SyncTagsAsync(db, book, write.Tags, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await BookRules.RemoveUnusedTagsAsync(db, cancellationToken);
        return TypedResults.Created($"/books/{book.Id}", BookResponse.From(book));
    }

    private static async Task<Results<Ok<BookResponse>, NotFound, ValidationProblem>> UpdateBook(
        int id,
        UpdateBookRequest request,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        var write = request.ToWrite();
        var problems = BookRules.Validate(write);
        if (problems is not null)
        {
            return TypedResults.ValidationProblem(problems);
        }

        var book = await db.Books.Include(existing => existing.Tags).FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (book is null)
        {
            return TypedResults.NotFound();
        }

        BookRules.Apply(book, write);
        await BookRules.SyncTagsAsync(db, book, write.Tags, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await BookRules.RemoveUnusedTagsAsync(db, cancellationToken);
        await db.Entry(book).Collection(existing => existing.Quotes).LoadAsync(cancellationToken);
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
        await BookRules.RemoveUnusedTagsAsync(db, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<QuoteResponse[]>, NotFound>> ListQuotes(
        int id,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        var exists = await db.Books.AnyAsync(book => book.Id == id, cancellationToken);
        if (!exists)
        {
            return TypedResults.NotFound();
        }

        var quotes = await db.Quotes.AsNoTracking()
            .Where(quote => quote.BookId == id)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(quotes
            .OrderBy(quote => quote.NotedAt)
            .ThenBy(quote => quote.Id)
            .Select(QuoteResponse.From)
            .ToArray());
    }

    private static async Task<Results<Created<QuoteResponse>, NotFound, ValidationProblem>> CreateQuote(
        int id,
        CreateQuoteRequest request,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        var book = await db.Books.FirstOrDefaultAsync(existing => existing.Id == id, cancellationToken);
        if (book is null)
        {
            return TypedResults.NotFound();
        }

        var problems = BookRules.ValidateQuote(request.Text, request.Page, book.Pages);
        if (problems is not null)
        {
            return TypedResults.ValidationProblem(problems);
        }

        var quote = new Quote
        {
            BookId = book.Id,
            Text = request.Text.Trim(),
            Page = request.Page,
            NotedAt = DateTimeOffset.UtcNow,
        };
        db.Quotes.Add(quote);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/books/{book.Id}/quotes/{quote.Id}", QuoteResponse.From(quote));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteQuote(
        int id,
        int quoteId,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        var quote = await db.Quotes.FirstOrDefaultAsync(
            existing => existing.Id == quoteId && existing.BookId == id,
            cancellationToken);
        if (quote is null)
        {
            return TypedResults.NotFound();
        }

        db.Quotes.Remove(quote);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}

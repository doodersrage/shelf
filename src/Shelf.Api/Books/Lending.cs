using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Books;

public enum LendResult
{
    Lent,
    NoBook,
    NoReader,
    ToSelf,
    AlreadyOut,
}

// Lending to another reader of this shelf: the book stays on the owner's shelf, and shows on the borrower's loans.
public static class Lending
{
    public static async Task<LendResult> LendAsync(
        ShelfDb db,
        int bookId,
        int readerId,
        DateOnly? dueOn,
        CancellationToken cancellationToken = default)
    {
        var book = await db.Books.FirstOrDefaultAsync(item => item.Id == bookId, cancellationToken);
        if (book is null)
        {
            return LendResult.NoBook;
        }

        if (readerId == db.ReaderId)
        {
            return LendResult.ToSelf;
        }

        var borrower = await db.Readers.AsNoTracking().FirstOrDefaultAsync(reader => reader.Id == readerId, cancellationToken);
        if (borrower is null)
        {
            return LendResult.NoReader;
        }

        if (book.LoanedTo is not null && book.BorrowerId != readerId)
        {
            return LendResult.AlreadyOut;
        }

        if (book.BorrowerId != readerId)
        {
            book.LoanedOn = DateOnly.FromDateTime(DateTime.UtcNow);
        }

        book.BorrowerId = borrower.Id;
        book.LoanedTo = borrower.Name;
        book.DueOn = dueOn;
        await db.SaveChangesAsync(cancellationToken);
        return LendResult.Lent;
    }

    public static async Task<BorrowedBook[]> BorrowedAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var readerId = db.ReaderId;
        var books = await db.Books.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(book => readerId != 0 && book.BorrowerId == readerId)
            .Select(book => new BorrowedBook(
                book.Id,
                book.Title,
                book.Author,
                book.CoverUrl,
                book.Owner == null ? "" : book.Owner.Name,
                book.LoanedOn,
                book.DueOn))
            .ToListAsync(cancellationToken);
        return books
            .OrderBy(book => book.DueOn ?? DateOnly.MaxValue)
            .ThenBy(book => book.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static async Task<bool> GiveBackAsync(ShelfDb db, int bookId, CancellationToken cancellationToken = default)
    {
        var readerId = db.ReaderId;
        var book = await db.Books.IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => readerId != 0 && item.Id == bookId && item.BorrowerId == readerId, cancellationToken);
        if (book is null)
        {
            return false;
        }

        BookRules.ReturnLoan(book);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public static string Describe(LendResult result) => result switch
    {
        LendResult.NoBook => "That book is not on your shelf.",
        LendResult.NoReader => "Choose a reader on this shelf.",
        LendResult.ToSelf => "The book is already yours.",
        LendResult.AlreadyOut => "The book is on loan. Mark it returned first.",
        _ => "",
    };

    public static async Task<Results<Ok<BookResponse>, NotFound, ValidationProblem>> Lend(
        int id,
        LendRequest request,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        var result = await LendAsync(db, id, request.ReaderId, request.DueOn, cancellationToken);
        if (result == LendResult.NoBook)
        {
            return TypedResults.NotFound();
        }

        if (result != LendResult.Lent)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(LendRequest.ReaderId)] = [Describe(result)],
            });
        }

        var book = await db.Books.AsNoTracking().WithDetails().FirstAsync(item => item.Id == id, cancellationToken);
        return TypedResults.Ok(BookResponse.From(book));
    }

    public static async Task<Ok<BorrowedBook[]>> Borrowed(ShelfDb db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await BorrowedAsync(db, cancellationToken));

    public static async Task<Results<NoContent, NotFound>> GiveBack(int id, ShelfDb db, CancellationToken cancellationToken) =>
        await GiveBackAsync(db, id, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound();
}

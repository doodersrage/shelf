using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Books;

public enum AskResult
{
    Asked,
    NoBook,
    AlreadyYours,
}

// A reader who opens their shelf lets the others see its titles and ask to borrow one.
// Only the catalog shows: never notes, reviews, quotes, highlights, or where the owner stopped.
public static class Asking
{
    public static async Task SetShelfOpenAsync(ShelfDb db, bool open, CancellationToken cancellationToken = default)
    {
        var reader = await db.Readers.FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        reader.ShelfOpen = open;
        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task<bool> IsShelfOpenAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        await db.Readers.AnyAsync(reader => reader.Id == db.ReaderId && reader.ShelfOpen, cancellationToken);

    public static async Task<OpenShelf[]> OpenShelvesAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var me = db.ReaderId;
        var readers = await db.Readers.AsNoTracking()
            .Where(reader => reader.ShelfOpen && reader.Id != me)
            .Select(reader => new { reader.Id, reader.Name })
            .ToListAsync(cancellationToken);
        var ids = readers.Select(reader => reader.Id).ToList();
        var counts = await db.Books.IgnoreQueryFilters()
            .Where(book => book.OwnerId != null && ids.Contains(book.OwnerId.Value))
            .GroupBy(book => book.OwnerId!.Value)
            .Select(group => new { Id = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Id, item => item.Count, cancellationToken);
        return readers
            .OrderBy(reader => reader.Name, StringComparer.OrdinalIgnoreCase)
            .Select(reader => new OpenShelf(reader.Id, reader.Name, counts.GetValueOrDefault(reader.Id)))
            .ToArray();
    }

    public static async Task<(string Owner, ShelfBook[] Books)?> ShelfAsync(ShelfDb db, int ownerId, CancellationToken cancellationToken = default)
    {
        var me = db.ReaderId;
        var owner = await db.Readers.AsNoTracking()
            .FirstOrDefaultAsync(reader => reader.Id == ownerId && reader.ShelfOpen && reader.Id != me, cancellationToken);
        if (owner is null || me == 0)
        {
            return null;
        }

        var asked = await db.LoanAsks.AsNoTracking()
            .Where(ask => ask.ReaderId == me)
            .Select(ask => ask.BookId)
            .ToListAsync(cancellationToken);
        var books = await db.Books.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(book => book.OwnerId == ownerId)
            .Select(book => new ShelfBook(
                book.Id,
                book.Title,
                book.Author,
                book.CoverUrl,
                book.Year,
                book.LoanedTo != null,
                book.EbookStoredName != null,
                book.AudioStoredName != null,
                false))
            .ToListAsync(cancellationToken);
        var shown = books
            .Select(book => book with { Asked = asked.Contains(book.Id) })
            .OrderBy(book => book.Author, StringComparer.OrdinalIgnoreCase)
            .ThenBy(book => book.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return (owner.Name, shown);
    }

    public static async Task<AskResult> AskAsync(ShelfDb db, int bookId, CancellationToken cancellationToken = default)
    {
        var me = db.ReaderId;
        var book = await db.Books.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => item.Id == bookId && item.OwnerId != me && item.Owner != null && item.Owner.ShelfOpen)
            .Select(item => new { item.Id, item.BorrowerId })
            .FirstOrDefaultAsync(cancellationToken);
        if (book is null || me == 0)
        {
            return AskResult.NoBook;
        }

        if (book.BorrowerId == me)
        {
            return AskResult.AlreadyYours;
        }

        if (!await db.LoanAsks.AnyAsync(ask => ask.BookId == bookId && ask.ReaderId == me, cancellationToken))
        {
            db.LoanAsks.Add(new LoanAskRow { BookId = bookId, ReaderId = me, AskedAt = DateTimeOffset.UtcNow });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Asked twice at once; the first one stands.
            }
        }

        return AskResult.Asked;
    }

    public static async Task<LoanAsks> AsksAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var me = db.ReaderId;
        var rows = await (
                from ask in db.LoanAsks.AsNoTracking()
                join book in db.Books.IgnoreQueryFilters() on ask.BookId equals book.Id
                join asker in db.Readers on ask.ReaderId equals asker.Id
                join owner in db.Readers on book.OwnerId equals (int?)owner.Id
                where me != 0 && (book.OwnerId == me || ask.ReaderId == me)
                select new
                {
                    ask.Id,
                    ask.BookId,
                    book.Title,
                    book.Author,
                    ask.ReaderId,
                    Asker = asker.Name,
                    OwnerId = owner.Id,
                    Owner = owner.Name,
                    ask.AskedAt,
                    OnLoan = book.LoanedTo != null,
                })
            .ToListAsync(cancellationToken);
        var ordered = rows.OrderBy(row => row.AskedAt).ThenBy(row => row.Id).ToList();
        return new LoanAsks(
            ordered.Where(row => row.OwnerId == me)
                .Select(row => new LoanAsk(row.Id, row.BookId, row.Title, row.Author, row.ReaderId, row.Asker, row.AskedAt, row.OnLoan))
                .ToArray(),
            ordered.Where(row => row.ReaderId == me)
                .Select(row => new LoanAsk(row.Id, row.BookId, row.Title, row.Author, row.OwnerId, row.Owner, row.AskedAt, row.OnLoan))
                .ToArray());
    }

    // The owner says yes: the book is lent to whoever asked, and the ask is done.
    public static async Task<LendResult> LendAskAsync(ShelfDb db, int askId, DateOnly? dueOn, CancellationToken cancellationToken = default)
    {
        var ask = await db.LoanAsks.FirstOrDefaultAsync(item => item.Id == askId, cancellationToken);
        if (ask is null || !await db.Books.AnyAsync(book => book.Id == ask.BookId, cancellationToken))
        {
            return LendResult.NoBook;
        }

        var result = await Lending.LendAsync(db, ask.BookId, ask.ReaderId, dueOn, cancellationToken);
        if (result == LendResult.Lent)
        {
            db.LoanAsks.Remove(ask);
            await db.SaveChangesAsync(cancellationToken);
        }

        return result;
    }

    // The owner declines, or the reader who asked takes it back.
    public static async Task<bool> DropAskAsync(ShelfDb db, int askId, CancellationToken cancellationToken = default)
    {
        var me = db.ReaderId;
        var ask = await db.LoanAsks.FirstOrDefaultAsync(item => item.Id == askId, cancellationToken);
        if (ask is null || me == 0)
        {
            return false;
        }

        var mine = ask.ReaderId == me || await db.Books.AnyAsync(book => book.Id == ask.BookId, cancellationToken);
        if (!mine)
        {
            return false;
        }

        db.LoanAsks.Remove(ask);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public static async Task<Reminders> RemindersAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var me = db.ReaderId;
        if (me == 0)
        {
            return new Reminders(0, 0, 0, 0);
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var soon = today.AddDays(3);
        var lentDue = await db.Books.AsNoTracking()
            .Where(book => book.LoanedTo != null && book.DueOn != null)
            .Select(book => book.DueOn!.Value)
            .ToListAsync(cancellationToken);
        var borrowedDue = await db.Books.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(book => book.BorrowerId == me && book.DueOn != null)
            .Select(book => book.DueOn!.Value)
            .ToListAsync(cancellationToken);
        var asks = await (
                from ask in db.LoanAsks
                join book in db.Books on ask.BookId equals book.Id
                select ask.Id)
            .CountAsync(cancellationToken);
        return new Reminders(
            lentDue.Count(due => due < today),
            borrowedDue.Count(due => due < today),
            borrowedDue.Count(due => due >= today && due <= soon),
            asks);
    }

    public static async Task<Ok<OpenShelf[]>> Shelves(ShelfDb db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await OpenShelvesAsync(db, cancellationToken));

    public static async Task<Results<Ok<ShelfBook[]>, NotFound>> Shelf(int id, ShelfDb db, CancellationToken cancellationToken) =>
        await ShelfAsync(db, id, cancellationToken) is { } shelf ? TypedResults.Ok(shelf.Books) : TypedResults.NotFound();

    public static async Task<NoContent> OpenShelf(ShelfOpenChange change, ShelfDb db, CancellationToken cancellationToken)
    {
        await SetShelfOpenAsync(db, change.Open, cancellationToken);
        return TypedResults.NoContent();
    }

    public static async Task<Results<NoContent, NotFound, ValidationProblem>> Ask(int id, ShelfDb db, CancellationToken cancellationToken) =>
        await AskAsync(db, id, cancellationToken) switch
        {
            AskResult.Asked => TypedResults.NoContent(),
            AskResult.AlreadyYours => TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["BookId"] = ["That book is already lent to you."],
            }),
            _ => TypedResults.NotFound(),
        };

    public static async Task<Ok<LoanAsks>> Asks(ShelfDb db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await AsksAsync(db, cancellationToken));

    public static async Task<Results<NoContent, NotFound, ValidationProblem>> LendAsk(
        int id,
        LendAskRequest request,
        ShelfDb db,
        CancellationToken cancellationToken) =>
        await LendAskAsync(db, id, request.DueOn, cancellationToken) switch
        {
            LendResult.Lent => TypedResults.NoContent(),
            LendResult.NoBook => TypedResults.NotFound(),
            var other => TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["ReaderId"] = [Lending.Describe(other)],
            }),
        };

    public static async Task<Results<NoContent, NotFound>> DropAsk(int id, ShelfDb db, CancellationToken cancellationToken) =>
        await DropAskAsync(db, id, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound();

    public static async Task<Ok<Reminders>> Remind(ShelfDb db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await RemindersAsync(db, cancellationToken));
}

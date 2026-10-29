using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// One loan of a copy, kept after it comes back: to whom, when, how often it was renewed, and the state it went out
// and came back in. Written as a save lends or returns the book, however that happens.
public sealed class LoanRecord
{
    public const int MaxNoteLength = 500;

    public int Id { get; set; }
    public int OwnerId { get; set; }
    public int BookId { get; set; }
    public Book? Book { get; set; }
    public int? BorrowerId { get; set; }
    public required string Borrower { get; set; }
    public DateOnly LentOn { get; set; }
    public DateOnly? DueOn { get; set; }
    public DateOnly? ReturnedOn { get; set; }
    public int Renewals { get; set; }
    public CopyCondition? ConditionOut { get; set; }
    public CopyCondition? ConditionBack { get; set; }
    public string? Note { get; set; }
}

public sealed record LoanRecordItem(int Id, int BookId, string Title, string Borrower, DateOnly LentOn, DateOnly? DueOn, DateOnly? ReturnedOn, int Renewals,
    CopyCondition? ConditionOut, CopyCondition? ConditionBack, string? Note);

public sealed record RenewRequest([Range(1, 365)] int? Days = null);

public sealed record ReturnRequest(CopyCondition? Condition = null, [MaxLength(LoanRecord.MaxNoteLength)] string? Note = null);

// Who has asked for a book, in the order they asked: the first in line is told when it comes back.
public sealed record WaitingLine(int BookId, string Title, bool OnLoan, string[] Readers);

public static class LoanHistory
{
    public const int DefaultRenewDays = 14;

    private static readonly AsyncLocal<bool> Quiet = new();

    // While a book comes back from the trash or a backup, its loans come back as they were kept, not as new ones.
    public static IDisposable Restoring()
    {
        var before = Quiet.Value;
        Quiet.Value = true;
        return new Undo(() => Quiet.Value = before);
    }

    private sealed class Undo(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    // Called as a save begins: a loan opened when a book goes out, closed when it comes back, its due date kept.
    public static void Note(ShelfDb db)
    {
        if (Quiet.Value)
        {
            return;
        }

        var today = db.Today;
        foreach (var entry in db.ChangeTracker.Entries<Book>().Where(item => item.State == EntityState.Added && item.Entity.LoanedTo is not null).ToList())
        {
            var book = entry.Entity;
            if (book.OwnerId is int owner)
            {
                db.LoanRecords.Add(new LoanRecord
                {
                    OwnerId = owner, Book = book, BorrowerId = book.BorrowerId, Borrower = book.LoanedTo!.Length > 120 ? book.LoanedTo[..120] : book.LoanedTo,
                    LentOn = book.LoanedOn ?? today, DueOn = book.DueOn, ConditionOut = book.Condition,
                });
            }
        }

        foreach (var entry in db.ChangeTracker.Entries<Book>().Where(item => item.State == EntityState.Modified).ToList())
        {
            var book = entry.Entity;
            var lentTo = entry.Property(item => item.LoanedTo);
            var due = entry.Property(item => item.DueOn);
            if (book.OwnerId is not int owner || (!lentTo.IsModified && !due.IsModified))
            {
                continue;
            }

            var (before, after) = (lentTo.OriginalValue, lentTo.CurrentValue);
            var sameLoan = before is not null && after is not null && before == after;
            if (sameLoan)
            {
                if (due.IsModified && Open(db, book.Id) is { } open)
                {
                    open.DueOn = book.DueOn;
                }

                continue;
            }

            if (before is not null && Open(db, book.Id) is { } closing)
            {
                closing.ReturnedOn = today;
                closing.ConditionBack = book.Condition;
            }

            if (after is not null)
            {
                db.LoanRecords.Add(new LoanRecord
                {
                    OwnerId = owner,
                    BookId = book.Id,
                    BorrowerId = book.BorrowerId,
                    Borrower = after.Length > 120 ? after[..120] : after,
                    LentOn = book.LoanedOn ?? today,
                    DueOn = book.DueOn,
                    ConditionOut = book.Condition,
                });
            }
        }
    }

    private static LoanRecord? Open(ShelfDb db, int bookId) =>
        db.LoanRecords.Local.FirstOrDefault(item => item.BookId == bookId && item.ReturnedOn == null)
        ?? db.LoanRecords.IgnoreQueryFilters().Where(item => item.BookId == bookId && item.ReturnedOn == null).OrderByDescending(item => item.Id).FirstOrDefault();

    // A loan made longer: by the days asked, or as long again as it first ran, or two weeks; from the due date, or
    // from today when it is already past.
    public static async Task<DateOnly?> RenewAsync(ShelfDb db, int bookId, int? days, CancellationToken cancellationToken = default)
    {
        var book = await db.Books.FirstOrDefaultAsync(item => item.Id == bookId && item.LoanedTo != null, cancellationToken);
        if (book is null)
        {
            return null;
        }

        var today = db.Today;
        var length = days is >= 1 and <= 365 ? days.Value
            : book is { LoanedOn: { } lent, DueOn: { } was } && was.DayNumber - lent.DayNumber is >= 1 and <= 365 and var first ? first
            : DefaultRenewDays;
        var from = book.DueOn is { } due && due > today ? due : today;
        book.DueOn = from.AddDays(length);
        await db.SaveChangesAsync(cancellationToken);
        await db.LoanRecords.Where(item => item.BookId == bookId && item.ReturnedOn == null)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.Renewals, item => item.Renewals + 1), cancellationToken);
        return book.DueOn;
    }

    // The owner marks a book back, saying the state it came back in and anything worth noting.
    public static async Task<bool> ReturnAsync(ShelfDb db, int bookId, CopyCondition? condition, string? note, CancellationToken cancellationToken = default)
    {
        var book = await db.Books.FirstOrDefaultAsync(item => item.Id == bookId, cancellationToken);
        if (book is null || book.LoanedTo is null)
        {
            return false;
        }

        var open = await db.LoanRecords.Where(item => item.BookId == bookId && item.ReturnedOn == null).OrderByDescending(item => item.Id).FirstOrDefaultAsync(cancellationToken);
        if (condition is { } state && Enum.IsDefined(state))
        {
            book.Condition = state;
        }

        BookRules.ReturnLoan(book);
        if (open is not null && !string.IsNullOrWhiteSpace(note))
        {
            open.Note = note.Trim().Length > LoanRecord.MaxNoteLength ? note.Trim()[..LoanRecord.MaxNoteLength] : note.Trim();
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public static async Task<LoanRecordItem[]> ForBookAsync(ShelfDb db, int bookId, CancellationToken cancellationToken = default) =>
        (await db.LoanRecords.AsNoTracking().Where(item => item.BookId == bookId).OrderByDescending(item => item.LentOn).ThenByDescending(item => item.Id)
            .Select(item => new { Record = item, item.Book!.Title }).ToListAsync(cancellationToken))
        .Select(item => From(item.Record, item.Title)).ToArray();

    public static async Task<LoanRecordItem[]> ReturnedAsync(ShelfDb db, int take = 100, CancellationToken cancellationToken = default) =>
        (await db.LoanRecords.AsNoTracking().Where(item => item.ReturnedOn != null).OrderByDescending(item => item.ReturnedOn).ThenByDescending(item => item.Id).Take(take)
            .Select(item => new { Record = item, item.Book!.Title }).ToListAsync(cancellationToken))
        .Select(item => From(item.Record, item.Title)).ToArray();

    // The owner's books with readers waiting for them, each line in the order they asked.
    public static async Task<WaitingLine[]> WaitingAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var rows = await (
                from ask in db.LoanAsks.AsNoTracking()
                join book in db.Books on ask.BookId equals book.Id
                join reader in db.Readers on ask.ReaderId equals reader.Id
                select new { ask.BookId, book.Title, OnLoan = book.LoanedTo != null, reader.Name, ask.AskedAt, ask.Id })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(row => row.BookId)
            .Where(group => group.Count() > 1 || group.First().OnLoan)
            .Select(group => new WaitingLine(group.Key, group.First().Title, group.First().OnLoan,
                [.. group.OrderBy(row => row.AskedAt).ThenBy(row => row.Id).Select(row => row.Name)]))
            .OrderBy(line => line.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    // Where this reader stands in line for a book: 1 is next.
    public static async Task<int?> PlaceInLineAsync(ShelfDb db, int bookId, CancellationToken cancellationToken = default)
    {
        var me = db.ReaderId;
        var line = await db.LoanAsks.AsNoTracking().Where(ask => ask.BookId == bookId).OrderBy(ask => ask.Id)
            .Select(ask => ask.ReaderId).ToListAsync(cancellationToken);
        var at = line.IndexOf(me);
        return at < 0 ? null : at + 1;
    }

    public static string Ordinal(int place) => place switch
    {
        1 => T("first in line"),
        2 => T("second in line"),
        3 => T("third in line"),
        _ => T("number {0} in line", place),
    };

    private static LoanRecordItem From(LoanRecord record, string title) => new(record.Id, record.BookId, title, record.Borrower, record.LentOn, record.DueOn,
        record.ReturnedOn, record.Renewals, record.ConditionOut, record.ConditionBack, record.Note);

    public static void Map(RouteGroupBuilder books)
    {
        books.MapGet("/{id:int}/loans", async Task<Results<Ok<LoanRecordItem[]>, NotFound>> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await db.Books.AnyAsync(book => book.Id == id, cancellationToken) ? TypedResults.Ok(await ForBookAsync(db, id, cancellationToken)) : TypedResults.NotFound()).WithTags("Lending");
        books.MapGet("/loans/history", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await ReturnedAsync(db, cancellationToken: cancellationToken))).WithTags("Lending");
        books.MapGet("/loans/waiting", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await WaitingAsync(db, cancellationToken))).WithTags("Lending");
        books.MapPost("/{id:int}/renew", async Task<Results<Ok<DateOnly>, NotFound>> (int id, RenewRequest? request, ShelfDb db, CancellationToken cancellationToken) =>
            await RenewAsync(db, id, request?.Days, cancellationToken) is { } due ? TypedResults.Ok(due) : TypedResults.NotFound()).WithTags("Lending");
    }
}

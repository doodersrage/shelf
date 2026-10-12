using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public sealed record AttentionItem(string Text, string Href);

// What on a shelf wants the reader now, for the top of Home: books lent out and overdue, borrowed books due back,
// readers asking to borrow, and new books in followed series and by followed authors. Empty, and Home shows nothing.
public static class Attention
{
    // News of new books, a few of each kind, so the list never crowds out the reading below it.
    private const int Few = 3;

    public static async Task<List<AttentionItem>> ForHomeAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var items = new List<AttentionItem>();
        if (db.ReaderId == 0)
        {
            return items;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var lateOut = await db.Books.AsNoTracking()
            .Where(book => book.LoanedTo != null && book.DueOn != null && book.DueOn < today)
            .OrderBy(book => book.DueOn)
            .Select(book => new { book.Id, book.Title, book.LoanedTo, book.DueOn })
            .ToListAsync(cancellationToken);
        items.AddRange(lateOut.Select(book => new AttentionItem(
            T("{0} is overdue from {1}, due {2}", book.Title, book.LoanedTo!, book.DueOn!.Value.Medium()), $"/library/{book.Id}#lending")));

        foreach (var book in (await Lending.BorrowedAsync(db, cancellationToken)).Where(book => book.DueOn is DateOnly due && due <= today.AddDays(3)))
        {
            var due = book.DueOn!.Value;
            items.Add(new AttentionItem(
                due < today ? T("{0} was due back to {1} on {2}", book.Title, book.Owner, due.Medium()) : T("{0} is due back to {1} {2}", book.Title, book.Owner, due.Medium()),
                "/loans"));
        }

        items.AddRange((await Asking.AsksAsync(db, cancellationToken)).Incoming
            .Select(ask => new AttentionItem(T("{0} asks to borrow {1}", ask.Reader, ask.Title), "/loans")));

        var series = await db.SeriesAlerts.AsNoTracking().Where(alert => !alert.Dismissed)
            .OrderByDescending(alert => alert.Id).Select(alert => new { alert.Series, alert.Title }).ToListAsync(cancellationToken);
        items.AddRange(series.Take(Few).Select(alert => new AttentionItem(T("New in {0}: {1}", alert.Series, alert.Title), "/series")));
        if (series.Count > Few)
        {
            items.Add(new AttentionItem(T("{0} more new in series you follow", series.Count - Few), "/series"));
        }

        var readerId = db.ReaderId;
        var authors = await db.AuthorAlerts.AsNoTracking().Where(alert => alert.ReaderId == readerId && !alert.Dismissed)
            .OrderByDescending(alert => alert.Id).Select(alert => new { alert.Author, alert.Title }).ToListAsync(cancellationToken);
        items.AddRange(authors.Take(Few).Select(alert => new AttentionItem(T("New from {0}: {1}", alert.Author, alert.Title), "/find")));
        if (authors.Count > Few)
        {
            items.Add(new AttentionItem(T("{0} more new by authors you follow", authors.Count - Few), "/find"));
        }

        return items;
    }
}

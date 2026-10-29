using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public enum CalendarKind
{
    Lent,
    Borrowed,
    Meeting,
    Plan,
    Goal,
}

// One day in a reader's calendar: a loan due either way, a club meeting (at its moment), a collection to be read by,
// or the end of the year for a goal.
public sealed record CalendarItem(string Uid, CalendarKind Kind, DateOnly Day, DateTimeOffset? At, string Text, string? Href, string? Zone = null);

// What a household's reading asks of one reader, and when: only what is theirs, or what they share with someone,
// such as a loan between them or a club they are both in. No one's reading is shown to anyone else here.
public static class HouseholdCalendar
{
    // Every item from a few weeks back to a year on, for the reader named, whoever is asking.
    public static async Task<List<CalendarItem>> ItemsAsync(ShelfDb db, int readerId, DateOnly from, DateOnly until, CancellationToken cancellationToken = default)
    {
        var items = new List<CalendarItem>();
        var books = db.Books.IgnoreQueryFilters().AsNoTracking();

        var lent = await books.Where(book => book.OwnerId == readerId && book.LoanedTo != null && book.DueOn != null && book.DueOn >= from && book.DueOn <= until)
            .Select(book => new { book.Id, book.Title, book.LoanedTo, Due = book.DueOn!.Value }).ToListAsync(cancellationToken);
        items.AddRange(lent.Select(book => new CalendarItem($"lent-{book.Id}", CalendarKind.Lent, book.Due, null, T("{0} is due back from {1}", book.Title, book.LoanedTo!), $"/library/{book.Id}")));

        var borrowed = await books.Where(book => book.BorrowerId == readerId && book.DueOn != null && book.DueOn >= from && book.DueOn <= until)
            .Select(book => new { book.Id, book.Title, Owner = book.Owner!.Name, Due = book.DueOn!.Value }).ToListAsync(cancellationToken);
        var visiting = await books.Where(book => book.OwnerId == readerId && book.VisitingDueOn != null && book.VisitingDueOn >= from && book.VisitingDueOn <= until)
            .Select(book => new { book.Id, book.Title, Owner = book.VisitingOwner ?? "", Due = book.VisitingDueOn!.Value }).ToListAsync(cancellationToken);
        items.AddRange(borrowed.Concat(visiting).Select(book => new CalendarItem($"borrowed-{book.Id}", CalendarKind.Borrowed, book.Due, null, T("Give {0} back to {1}", book.Title, book.Owner), "/loans")));

        // Meetings of the clubs the reader is in. Their moments are compared here, as SQLite cannot.
        var meetings = await db.ClubMeetings.IgnoreQueryFilters().AsNoTracking()
            .Where(meeting => db.ClubMembers.Any(member => member.ClubId == meeting.ClubId && member.ReaderId == readerId))
            .Select(meeting => new { meeting.Id, meeting.ClubId, meeting.At, meeting.TimeZone, Club = meeting.Club!.Name, Book = meeting.Book!.Title, meeting.Through })
            .ToListAsync(cancellationToken);
        foreach (var meeting in meetings)
        {
            var day = DateOnly.FromDateTime(meeting.At.UtcDateTime);
            if (day >= from && day <= until)
            {
                items.Add(new CalendarItem($"meeting-{meeting.Id}", CalendarKind.Meeting, day, meeting.At,
                    meeting.Through is { Length: > 0 } through ? T("{0} meets about {1}, through {2}", meeting.Club, meeting.Book, through) : T("{0} meets about {1}", meeting.Club, meeting.Book),
                    $"/clubs/{meeting.ClubId}", meeting.TimeZone));
            }
        }

        // Collections to be read by a day: the reader's own, and for a child, those their guardian shares with them.
        var guardian = await db.Readers.AsNoTracking().Where(reader => reader.Id == readerId && reader.Restricted).Select(reader => reader.GuardianId).FirstOrDefaultAsync(cancellationToken);
        var plans = await db.Collections.IgnoreQueryFilters().AsNoTracking()
            .Where(collection => collection.ReadBy != null && collection.ReadBy >= from && collection.ReadBy <= until
                && (collection.OwnerId == readerId || (guardian != null && collection.OwnerId == guardian && collection.ForChildren)))
            .Select(collection => new { collection.Id, collection.Name, Day = collection.ReadBy!.Value, collection.OwnerId }).ToListAsync(cancellationToken);
        items.AddRange(plans.Select(plan => new CalendarItem($"plan-{plan.Id}", CalendarKind.Plan, plan.Day, null, T("Read {0} by today", plan.Name), $"/collections/{plan.Id}")));

        // Goals of the reader's own end with their year.
        var goals = await db.ReadingGoals.IgnoreQueryFilters().AsNoTracking().Where(goal => goal.OwnerId == readerId && goal.Year >= from.Year && goal.Year <= until.Year).ToListAsync(cancellationToken);
        foreach (var goal in goals)
        {
            var last = new DateOnly(goal.Year, 12, 31);
            if (last >= from && last <= until)
            {
                items.Add(new CalendarItem($"goal-{goal.Id}", CalendarKind.Goal, last, null, T("Last day for your goal: {0}", ReadingGoals.Label(goal.Kind, goal.Name, goal.Target)), "/stats"));
            }
        }

        return [.. items.OrderBy(item => item.Day).ThenBy(item => item.At ?? DateTimeOffset.MinValue).ThenBy(item => item.Text, StringComparer.CurrentCultureIgnoreCase)];
    }

    public static void Map(RouteGroupBuilder books) =>
        books.MapGet("/agenda", async (DateOnly? from, DateOnly? until, ShelfDb db, CancellationToken cancellationToken) =>
        {
            var today = db.Today;
            var start = from ?? today.AddDays(-14);
            var end = until is DateOnly last && last >= start && last <= start.AddYears(2) ? last : start.AddYears(1);
            return TypedResults.Ok(await ItemsAsync(db, db.ReaderId, start, end, cancellationToken));
        });
}

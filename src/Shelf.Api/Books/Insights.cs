using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public sealed record ActivityDay(DateOnly Day, int Minutes, int Pages, int Level);

public sealed record Favourite(string Name, int Count, string Href);

public sealed record Milestone(DateOnly Day, string Text, int? BookId);

public sealed record YearOfReading(ActivityDay[] Days, int ActiveDays, int LongestRun, string? BusiestMonth);

// The story a shelf tells: a year of days read and listened, the authors, kinds, and series returned to, and the
// milestones passed on the way. Counted from what is logged; nothing here asks for a streak to be kept.
public static class Insights
{
    private static readonly int[] FinishedMarks = [1, 10, 25, 50, 100, 250, 500, 1000];
    private static readonly int[] PageMarks = [10_000, 25_000, 50_000, 100_000, 250_000, 500_000];

    // The last 53 weeks, Monday to Sunday, ending this week: minutes with a book (reading and listening) and pages
    // logged each day, and a level from 0 to 4 for how full the day was.
    public static async Task<YearOfReading> YearAsync(ShelfDb db, DateOnly today, CancellationToken cancellationToken = default)
    {
        var end = today.AddDays(6 - ((int)today.DayOfWeek + 6) % 7);
        var start = end.AddDays(-7 * 53 + 1);
        var seconds = new Dictionary<DateOnly, int>();
        foreach (var (day, spent) in (await db.ReadingDays.AsNoTracking().Where(item => item.Day >= start).Select(item => new { item.Day, item.Seconds }).ToListAsync(cancellationToken)).Select(item => (item.Day, item.Seconds))
            .Concat((await db.ListeningDays.AsNoTracking().Where(item => item.Day >= start).Select(item => new { item.Day, item.Seconds }).ToListAsync(cancellationToken)).Select(item => (item.Day, item.Seconds))))
        {
            seconds[day] = seconds.GetValueOrDefault(day) + spent;
        }

        var pages = new Dictionary<DateOnly, int>();
        var logged = new HashSet<DateOnly>();
        foreach (var session in await db.Sessions.AsNoTracking().Where(item => item.Date >= start).Select(item => new { item.Date, item.FromPage, item.ToPage }).ToListAsync(cancellationToken))
        {
            logged.Add(session.Date);
            if (session.FromPage is int from && session.ToPage is int to && to > from)
            {
                pages[session.Date] = pages.GetValueOrDefault(session.Date) + to - from;
            }
        }

        var days = new List<ActivityDay>();
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            var minutes = seconds.GetValueOrDefault(day) / 60;
            var read = pages.GetValueOrDefault(day);
            // A page is counted as about two minutes, so a logged session weighs like time in the reader.
            var weight = minutes + read * 2;
            var level = day > today ? -1
                : weight >= 90 ? 4
                : weight >= 45 ? 3
                : weight >= 15 ? 2
                : weight > 0 || logged.Contains(day) ? 1
                : 0;
            days.Add(new ActivityDay(day, minutes, read, level));
        }

        var active = days.Where(day => day.Level > 0).ToList();
        var (longest, run) = (0, 0);
        foreach (var day in days.Where(day => day.Level >= 0))
        {
            run = day.Level > 0 ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        var busiest = active.GroupBy(day => new DateOnly(day.Day.Year, day.Day.Month, 1))
            .OrderByDescending(group => group.Sum(day => day.Minutes + day.Pages * 2)).ThenByDescending(group => group.Key)
            .Select(group => group.Key.MonthYear()).FirstOrDefault();
        return new YearOfReading([.. days], active.Count, longest, busiest);
    }

    // The authors finished most, the kinds of book (by tag) finished most, and the series followed furthest.
    public static (Favourite[] Authors, Favourite[] Kinds, Favourite[] Series) Favourites(IReadOnlyCollection<Book> books, int count = 5)
    {
        var finished = books.Where(book => book.Status == BookStatus.Finished).ToList();
        var authors = finished.GroupBy(book => book.Author.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .Where(group => group.Key.Length > 0)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase).Take(count)
            .Select(group => new Favourite(group.Key, group.Count(), $"/library?author={Uri.EscapeDataString(group.Key)}"));
        var kinds = finished.SelectMany(book => book.Tags.Select(tag => tag.Name)).GroupBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase).Take(count)
            .Select(group => new Favourite(group.Key, group.Count(), $"/library?tag={Uri.EscapeDataString(group.Key)}"));
        var series = books.Where(book => !string.IsNullOrWhiteSpace(book.Series)).GroupBy(book => book.Series!.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .Select(group => (group.Key, Read: group.Count(book => book.Status == BookStatus.Finished)))
            .Where(item => item.Read > 0)
            .OrderByDescending(item => item.Read).ThenBy(item => item.Key, StringComparer.CurrentCultureIgnoreCase).Take(count)
            .Select(item => new Favourite(item.Key, item.Read, $"/library?series={Uri.EscapeDataString(item.Key)}"));
        return ([.. authors], [.. kinds], [.. series]);
    }

    // Moments in a reader's history, newest first: the first book finished, the tenth, the hundredth; pages passing
    // ten thousand; the longest book finished; this year's goal reached.
    public static Milestone[] Milestones(IReadOnlyCollection<Book> books, int yearlyGoal, DateOnly today)
    {
        var finished = books.Where(book => book.Status == BookStatus.Finished && book.FinishedOn is not null)
            .OrderBy(book => book.FinishedOn).ThenBy(book => book.Id).ToList();
        var milestones = new List<Milestone>();
        var pages = 0;
        var nextPageMark = 0;
        for (var index = 0; index < finished.Count; index++)
        {
            var book = finished[index];
            var day = book.FinishedOn!.Value;
            var number = index + 1;
            if (FinishedMarks.Contains(number))
            {
                milestones.Add(new Milestone(day, number == 1 ? T("Your first finished book on Shelf: {0}", book.Title) : T("Book {0} finished: {1}", number, book.Title), book.Id));
            }

            pages += book.Pages ?? 0;
            while (nextPageMark < PageMarks.Length && pages >= PageMarks[nextPageMark])
            {
                milestones.Add(new Milestone(day, T("{0} pages read, with {1}", PageMarks[nextPageMark].ToString("N0"), book.Title), book.Id));
                nextPageMark++;
            }
        }

        if (finished.Where(book => book.Pages is not null).MaxBy(book => book.Pages) is { } longest && longest.Pages >= 300)
        {
            milestones.Add(new Milestone(longest.FinishedOn!.Value, T("The longest book you've finished: {0}, {1} pages", longest.Title, longest.Pages ?? 0), longest.Id));
        }

        var thisYear = finished.Where(book => book.FinishedOn!.Value.Year == today.Year).ToList();
        if (yearlyGoal > 0 && thisYear.Count >= yearlyGoal)
        {
            var reached = thisYear[yearlyGoal - 1];
            milestones.Add(new Milestone(reached.FinishedOn!.Value, T("This year's goal of {0} reached with {1}", yearlyGoal, reached.Title), reached.Id));
        }

        return [.. milestones.OrderByDescending(item => item.Day).ThenByDescending(item => item.Text.Length)];
    }
}

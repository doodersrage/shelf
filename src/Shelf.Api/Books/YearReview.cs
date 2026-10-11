using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

public sealed record ReviewBook(int Id, string Title, string Author, string? CoverUrl, int? Pages, int? Rating, DateOnly? FinishedOn);

public sealed record ReviewCount(string Name, int Count);

// A reader's year in books: what they finished and when, how long they spent, and what stood out.
public sealed record YearReviewResponse(
    int Year,
    int Finished,
    int Goal,
    int Pages,
    int ReadingSeconds,
    int ListeningSeconds,
    int Authors,
    int NewAuthors,
    int[] ByMonth,
    ReviewCount[] Formats,
    ReviewCount[] Tags,
    ReviewBook? Longest,
    ReviewBook? Shortest,
    ReviewBook? First,
    ReviewBook? Last,
    ReviewBook[] Loved,
    ReviewBook[] Books,
    int Quotes,
    int Highlights,
    string? FavouriteQuote,
    string? FavouriteQuoteBook);

public static class YearReview
{
    public static void Map(RouteGroupBuilder books) =>
        books.MapGet("/years/{year:int}/review", async Task<Results<Ok<YearReviewResponse>, NotFound>> (int year, ShelfDb db, CancellationToken cancellationToken) =>
            year is < 1000 or > 2100 ? TypedResults.NotFound() : TypedResults.Ok(await BuildAsync(db, year, cancellationToken)));

    public static async Task<YearReviewResponse> BuildAsync(ShelfDb db, int year, CancellationToken cancellationToken = default)
    {
        var all = await db.Books.AsNoTracking().Include(book => book.Tags).AsSplitQuery()
            .Where(book => book.Status == BookStatus.Finished && book.FinishedOn != null)
            .ToListAsync(cancellationToken);
        var finished = all.Where(book => book.FinishedOn!.Value.Year == year)
            .OrderBy(book => book.FinishedOn)
            .ThenBy(book => book.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // An author is new in a year when none of their books was finished before it.
        var authorsBefore = all.Where(book => book.FinishedOn!.Value.Year < year).Select(book => book.Author.Trim().ToLowerInvariant()).ToHashSet();
        var authors = finished.Select(book => book.Author.Trim().ToLowerInvariant()).Distinct().ToList();

        var me = db.ReaderId;
        var start = new DateOnly(year, 1, 1);
        var end = new DateOnly(year, 12, 31);
        var reading = await db.ReadingDays.IgnoreQueryFilters().Where(day => day.ReaderId == me && day.Day >= start && day.Day <= end).SumAsync(day => (int?)day.Seconds, cancellationToken) ?? 0;
        var listening = await db.ListeningDays.IgnoreQueryFilters().Where(day => day.ReaderId == me && day.Day >= start && day.Day <= end).SumAsync(day => (int?)day.Seconds, cancellationToken) ?? 0;

        var from = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var until = from.AddYears(1);
        var quotes = (await db.Quotes.AsNoTracking().Where(quote => quote.Book!.OwnerId == me)
            .Select(quote => new { quote.Text, quote.NotedAt, Title = quote.Book!.Title })
            .ToListAsync(cancellationToken))
            .Where(quote => quote.NotedAt >= from && quote.NotedAt < until)
            .ToList();
        var highlights = (await db.Highlights.IgnoreQueryFilters().AsNoTracking()
            .Where(mark => (mark.ReaderId == null && mark.Book!.OwnerId == me) || mark.ReaderId == me)
            .Select(mark => mark.NotedAt)
            .ToListAsync(cancellationToken))
            .Count(noted => noted >= from && noted < until);

        // The quote to remember the year by: the longest one that still fits in a breath.
        var favourite = quotes.Where(quote => quote.Text.Length <= 280).OrderByDescending(quote => quote.Text.Length).FirstOrDefault();
        var measured = finished.Where(book => book.Pages is > 0).ToList();
        var goal = year == DateTime.UtcNow.Year ? await BookRules.GetGoalAsync(db, cancellationToken) : 0;

        return new YearReviewResponse(
            year,
            finished.Count,
            goal,
            finished.Sum(book => book.Pages ?? 0),
            reading,
            listening,
            authors.Count,
            authors.Count(author => !authorsBefore.Contains(author)),
            Enumerable.Range(1, 12).Select(month => finished.Count(book => book.FinishedOn!.Value.Month == month)).ToArray(),
            finished.GroupBy(book => book.Format?.ToString() ?? "")
                .Where(group => group.Key.Length > 0)
                .Select(group => new ReviewCount(group.Key, group.Count()))
                .OrderByDescending(group => group.Count)
                .ToArray(),
            finished.SelectMany(book => book.Tags.Select(tag => tag.Name)).GroupBy(name => name)
                .Select(group => new ReviewCount(group.Key, group.Count()))
                .OrderByDescending(group => group.Count).ThenBy(group => group.Name, StringComparer.Ordinal)
                .Take(6)
                .ToArray(),
            Show(measured.MaxBy(book => book.Pages)),
            measured.Count > 1 ? Show(measured.MinBy(book => book.Pages)) : null,
            Show(finished.FirstOrDefault()),
            finished.Count > 1 ? Show(finished.LastOrDefault()) : null,
            finished.Where(book => book.Loved || book.Rating == 5).Select(Show).OfType<ReviewBook>().ToArray(),
            finished.Select(Show).OfType<ReviewBook>().ToArray(),
            quotes.Count,
            highlights,
            favourite?.Text,
            favourite?.Title);
    }

    // The years with something finished in them, newest first, for links to each review.
    public static async Task<int[]> YearsAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        (await db.Books.AsNoTracking().Where(book => book.Status == BookStatus.Finished && book.FinishedOn != null).Select(book => book.FinishedOn!.Value.Year).Distinct().ToListAsync(cancellationToken))
            .OrderDescending()
            .ToArray();

    private static ReviewBook? Show(Book? book) =>
        book is null ? null : new ReviewBook(book.Id, book.Title, book.Author, book.CoverShown, book.Pages, book.Rating, book.FinishedOn);
}

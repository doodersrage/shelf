using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

// Time spent with a book open in the reader, by one reader on one day: counted only while the page is in view and
// the reader has scrolled, turned a page, or touched it in the last two minutes.
public sealed class ReadingDay
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public Book? Book { get; set; }
    public int ReaderId { get; set; }
    public DateOnly Day { get; set; }
    public int Seconds { get; set; }
}

public sealed record ReadingTimeRequest(DateOnly? Day, double Seconds);

public static class ReadingTime
{
    // The reader says every minute or so; more than five minutes at once is not believed.
    private const int MostAtOnce = 300;

    public static void Map(RouteGroupBuilder books)
    {
        books.MapPost("/{id:int}/reading-time", Note);
        books.MapGet("/reading-time", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await SummaryAsync(db, cancellationToken: cancellationToken)));
    }

    public static async Task<ListeningSummary> SummaryAsync(ShelfDb db, DateOnly? today = null, CancellationToken cancellationToken = default)
    {
        // A borrower's time on a book lent to them counts too, so the books' own filter is set aside.
        var me = db.ReaderId;
        var rows = await db.ReadingDays.IgnoreQueryFilters().AsNoTracking()
            .Where(item => item.ReaderId == me && me != 0)
            .Select(item => new AudioPlayback.TimeRow(item.BookId, item.Day, item.Seconds, item.Book!.Title, item.Book.Author))
            .ToListAsync(cancellationToken);
        return AudioPlayback.Summarize(rows, today ?? DateOnly.FromDateTime(DateTime.UtcNow));
    }

    private static async Task<Results<NoContent, NotFound>> Note(int id, ReadingTimeRequest request, ShelfDb db, CancellationToken cancellationToken)
    {
        if (await Lending.OpenAsync(db, id, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }

        if (request.Seconds < 1 || double.IsNaN(request.Seconds))
        {
            return TypedResults.NoContent();
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var when = request.Day is { } given && Math.Abs(given.DayNumber - today.DayNumber) <= 1 ? given : today;
        var seconds = (int)Math.Round(Math.Min(request.Seconds, MostAtOnce));
        var row = await db.ReadingDays.FirstOrDefaultAsync(item => item.BookId == id && item.Day == when, cancellationToken);
        if (row is null)
        {
            db.ReadingDays.Add(new ReadingDay { BookId = id, ReaderId = db.ReaderId, Day = when, Seconds = seconds });
        }
        else
        {
            row.Seconds += seconds;
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}

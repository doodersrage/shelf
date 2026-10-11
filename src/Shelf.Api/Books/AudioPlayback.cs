using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

// Everything the player needs to play a book as one recording: its tracks and how long each runs, its chapters
// (the marks inside a single file, or else one for each track), where this reader stopped, and their speed.
public sealed record AudioPlan(
    int BookId,
    string Title,
    string Author,
    string? Cover,
    AudioPlanTrack[] Tracks,
    AudioPlanChapter[] Chapters,
    int Track,
    double Seconds,
    int Speed,
    bool HasEbook);

public sealed record AudioPlanTrack(int Index, string Title, double? Length, string Url);

// A chapter starts in a track, so many tracks or one long file are told the same way.
public sealed record AudioPlanChapter(int Index, string Title, int Track, double Start);

// Listened is how many seconds the player played since it last said, by the clock rather than the book, and Day
// the listener's own date, so time listened lands on the day it was where they are.
public sealed record AudioPlaceRequest(int Track, double Seconds, double? Listened = null, DateOnly? Day = null);

// Time spent listening to one book on one day, by one reader.
public sealed class ListeningDay
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public Book? Book { get; set; }
    public int ReaderId { get; set; }
    public DateOnly Day { get; set; }
    public int Seconds { get; set; }
}

public sealed record ListeningSummary(int TodaySeconds, int WeekSeconds, int YearSeconds, int TotalSeconds, ListeningDayTotal[] LastDays, ListeningBook[] TopBooks);

public sealed record ListeningDayTotal(DateOnly Day, int Seconds);

public sealed record ListeningBook(int BookId, string Title, string Author, int Seconds);

public sealed record AudioSpeedRequest(int Speed);

public sealed record FinishedResponse(bool Marked);

public static class AudioPlayback
{
    public static void Map(RouteGroupBuilder books)
    {
        books.MapGet("/{id:int}/audio/plan", Plan);
        books.MapPut("/{id:int}/audio/place", Place);
        books.MapPut("/audio/speed", Speed);
        books.MapPost("/{id:int}/audio/finished", Finished);
        books.MapGet("/listening", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await SummaryAsync(db, cancellationToken: cancellationToken)));
    }

    public static async Task<AudioPlan?> PlanAsync(ShelfDb db, AudioStore store, int id, CancellationToken cancellationToken = default)
    {
        if (await Lending.OpenAsync(db, id, cancellationToken) is not { } open || string.IsNullOrWhiteSpace(open.Book.AudioStoredName))
        {
            return null;
        }

        var book = open.Book;
        var tracks = store.Tracks(book.AudioStoredName);
        if (tracks.Count == 0)
        {
            return null;
        }

        var lengths = store.Lengths(book.AudioStoredName);
        var marks = store.Chapters(book.AudioStoredName);
        var chapters = marks.Count > 1
            ? marks.Select(mark => new AudioPlanChapter(mark.Index, mark.Title, 0, mark.Start)).ToArray()
            : tracks.Select(track => new AudioPlanChapter(track.Index, track.Title, track.Index, 0)).ToArray();
        var place = await Lending.PlaceAsync(db, open, cancellationToken);
        var at = place.AudioTrack is int saved && saved >= 0 && saved < tracks.Count ? saved : 0;
        return new AudioPlan(
            book.Id,
            book.Title,
            book.Author,
            book.CoverShown,
            tracks.Select(track => new AudioPlanTrack(track.Index, track.Title, lengths[track.Index], $"/books/{book.Id}/audio/tracks/{track.Index}")).ToArray(),
            chapters,
            at,
            Math.Max(0, place.AudioSeconds ?? 0),
            await BookRules.GetAudioSpeedAsync(db, cancellationToken),
            EbookStore.IsEpub(book.EbookStoredName));
    }

    private static async Task<Results<Ok<AudioPlan>, NotFound>> Plan(int id, ShelfDb db, AudioStore store, CancellationToken cancellationToken) =>
        await PlanAsync(db, store, id, cancellationToken) is { } plan ? TypedResults.Ok(plan) : TypedResults.NotFound();

    // Where the player is: kept as it is, back or forward, since a listener moves both ways.
    private static async Task<Results<NoContent, NotFound, ValidationProblem>> Place(int id, AudioPlaceRequest request, ShelfDb db, AudioStore store, CancellationToken cancellationToken)
    {
        if (await Lending.OpenAsync(db, id, cancellationToken) is not { } open || string.IsNullOrWhiteSpace(open.Book.AudioStoredName))
        {
            return TypedResults.NotFound();
        }

        if (request.Track < 0 || request.Track >= store.Tracks(open.Book.AudioStoredName).Count || request.Seconds is < 0 or > 1_000_000 || double.IsNaN(request.Seconds))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Track"] = [Localization.Words.T("Give a track of this recording, and a time in it.")] });
        }

        await Lending.KeepPlaceAsync(db, open, place =>
        {
            place.AudioTrack = request.Track;
            place.AudioSeconds = (int)request.Seconds;
        }, cancellationToken);
        if (request.Listened is double listened && listened >= 1)
        {
            await NoteListeningAsync(db, open.Book.Id, request.Day, listened, cancellationToken);
        }

        return TypedResults.NoContent();
    }

    // The player says every fifteen seconds or so; more than two minutes at once is not believed.
    private const int MostAtOnce = 120;

    public static async Task NoteListeningAsync(ShelfDb db, int bookId, DateOnly? day, double listened, CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var when = day is { } given && Math.Abs(given.DayNumber - today.DayNumber) <= 1 ? given : today;
        var seconds = (int)Math.Round(Math.Min(listened, MostAtOnce));
        var row = await db.ListeningDays.FirstOrDefaultAsync(item => item.BookId == bookId && item.Day == when, cancellationToken);
        if (row is null)
        {
            db.ListeningDays.Add(new ListeningDay { BookId = bookId, ReaderId = db.ReaderId, Day = when, Seconds = seconds });
        }
        else
        {
            row.Seconds += seconds;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task<ListeningSummary> SummaryAsync(ShelfDb db, DateOnly? today = null, CancellationToken cancellationToken = default)
    {
        var day = today ?? DateOnly.FromDateTime(DateTime.UtcNow);
        // Books lent to the listener count too, so the books' own filter (the owner's shelf) is set aside here.
        var me = db.ReaderId;
        var rows = await db.ListeningDays.IgnoreQueryFilters().AsNoTracking()
            .Where(item => item.ReaderId == me && me != 0)
            .Select(item => new { item.BookId, item.Day, item.Seconds, Title = item.Book!.Title, Author = item.Book.Author })
            .ToListAsync(cancellationToken);
        var weekStart = day.AddDays(-6);
        var lastDays = Enumerable.Range(0, 14)
            .Select(back => day.AddDays(back - 13))
            .Select(date => new ListeningDayTotal(date, rows.Where(row => row.Day == date).Sum(row => row.Seconds)))
            .ToArray();
        var top = rows.Where(row => row.Day.Year == day.Year)
            .GroupBy(row => row.BookId)
            .Select(group => new ListeningBook(group.Key, group.First().Title, group.First().Author, group.Sum(row => row.Seconds)))
            .OrderByDescending(book => book.Seconds)
            .Take(5)
            .ToArray();
        return new ListeningSummary(
            rows.Where(row => row.Day == day).Sum(row => row.Seconds),
            rows.Where(row => row.Day >= weekStart && row.Day <= day).Sum(row => row.Seconds),
            rows.Where(row => row.Day.Year == day.Year).Sum(row => row.Seconds),
            rows.Sum(row => row.Seconds),
            lastDays,
            top);
    }

    // The end of the recording: the owner's book is finished, unless it already was. A borrower's ending changes
    // nothing on the owner's shelf.
    private static async Task<Results<Ok<FinishedResponse>, NotFound>> Finished(int id, ShelfDb db, CancellationToken cancellationToken)
    {
        var book = await db.Books.FirstOrDefaultAsync(item => item.Id == id && item.AudioStoredName != null, cancellationToken);
        if (book is null)
        {
            return await Lending.OpenAsync(db, id, cancellationToken) is null ? TypedResults.NotFound() : TypedResults.Ok(new FinishedResponse(false));
        }

        if (book.Status == BookStatus.Finished)
        {
            return TypedResults.Ok(new FinishedResponse(false));
        }

        BookRules.ChangeStatus(book, BookStatus.Finished);
        book.AudioTrack = 0;
        book.AudioSeconds = 0;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new FinishedResponse(true));
    }

    private static async Task<NoContent> Speed(AudioSpeedRequest request, ShelfDb db, CancellationToken cancellationToken)
    {
        await BookRules.SetAudioSpeedAsync(db, request.Speed, cancellationToken);
        return TypedResults.NoContent();
    }
}

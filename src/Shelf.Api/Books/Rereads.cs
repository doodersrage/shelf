using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// An earlier read of a book read again: its dates, the stars it had then, and what the reader thinks of it now. The
// book itself holds the read under way, or the latest; each earlier finish still counts in its own year.
public sealed class PastRead
{
    public const int MaxThoughtsLength = 4000;

    public int Id { get; set; }
    public int BookId { get; set; }
    public Book? Book { get; set; }
    public DateOnly? StartedOn { get; set; }
    public DateOnly? FinishedOn { get; set; }
    public int? Rating { get; set; }
    public string? Thoughts { get; set; }
}

public sealed record PastReadResponse(int Id, int BookId, DateOnly? StartedOn, DateOnly? FinishedOn, int? Rating, string? Thoughts)
{
    public static PastReadResponse From(PastRead read) => new(read.Id, read.BookId, read.StartedOn, read.FinishedOn, read.Rating, read.Thoughts);
}

public sealed record PastReadRequest([Range(1, 5)] int? Rating, [MaxLength(PastRead.MaxThoughtsLength)] string? Thoughts);

// One read of a book, the earlier ones and the one now, with what was written and marked while it lasted.
public sealed record ReadThrough(
    int? PastReadId,
    int Number,
    DateOnly? StartedOn,
    DateOnly? FinishedOn,
    int? Rating,
    string? Thoughts,
    int Sessions,
    int Highlights,
    int Quotes,
    bool Current);

// A finish that counts in its year: the book's own, or an earlier one of a book read again.
public sealed record Finish(int BookId, string Title, string Author, DateOnly FinishedOn, int? Pages, int? Rating, bool Again);

public static class Rereads
{
    public static void Map(RouteGroupBuilder books)
    {
        books.MapPost("/{id:int}/again", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await ReadAgainAsync(db, id, cancellationToken) switch
            {
                AgainResult.Started => TypedResults.NoContent(),
                AgainResult.NotFinished => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Status"] = [T("Only a finished book can be read again.")] }),
                _ => TypedResults.NotFound(),
            });
        books.MapGet("/{id:int}/reads", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await ThroughsAsync(db, id, cancellationToken) is { } reads ? TypedResults.Ok(reads) : TypedResults.NotFound());
        books.MapPut("/{id:int}/reads/{readId:int}", async Task<IResult> (int id, int readId, PastReadRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            await UpdateAsync(db, id, readId, request.Rating, request.Thoughts, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());
        books.MapDelete("/{id:int}/reads/{readId:int}", async Task<IResult> (int id, int readId, ShelfDb db, CancellationToken cancellationToken) =>
            await db.PastReads.Where(read => read.Id == readId && read.BookId == id).ExecuteDeleteAsync(cancellationToken) > 0 ? TypedResults.NoContent() : TypedResults.NotFound());
    }

    public enum AgainResult
    {
        Started,
        NotFinished,
        NotFound,
    }

    // The finished read is put by with its dates and stars, and the book starts again from the beginning: Reading
    // from today, its place in the file and on paper cleared. The stars stay on the book until the reader changes them.
    // Which time through this is, as the earlier reads counted: sent with the book to a page that may queue places,
    // and back with a place sent late, so one from an earlier read never moves a reread on.
    public static Task<int> ReadNumberAsync(ShelfDb db, int bookId, CancellationToken cancellationToken = default) =>
        db.PastReads.CountAsync(read => read.BookId == bookId, cancellationToken);

    public static async Task<AgainResult> ReadAgainAsync(ShelfDb db, int bookId, CancellationToken cancellationToken = default)
    {
        var book = await db.Books.FirstOrDefaultAsync(item => item.Id == bookId, cancellationToken);
        if (book is null)
        {
            return AgainResult.NotFound;
        }

        if (book.Status != BookStatus.Finished)
        {
            return AgainResult.NotFinished;
        }

        db.PastReads.Add(new PastRead { BookId = book.Id, StartedOn = book.StartedOn, FinishedOn = book.FinishedOn, Rating = book.Rating });
        book.StartedOn = null;
        book.FinishedOn = null;
        book.CurrentPage = null;
        book.EbookChapter = null;
        book.EbookCfi = null;
        book.EbookProgress = null;
        book.AudioTrack = null;
        book.AudioSeconds = null;
        BookRules.ChangeStatus(book, BookStatus.Reading, db.Today);

        // Devices still hold the last time through. KOReader's place is kept but marked, so the device saying it
        // again doesn't jump the reread to the end; the Kobo is told at its next sync that the book is being read
        // again, from the start; and where each device had the reader goes, as it was the last read's.
        var readerId = db.ReaderId;
        foreach (var kept in await db.KosyncPlaces.Where(item => item.ReaderId == readerId && item.BookId == book.Id).ToListAsync(cancellationToken))
        {
            kept.Chapter = KosyncPlace.BeforeReread;
        }

        foreach (var entry in await db.KoboEntries.Where(item => item.ReaderId == readerId && item.BookId == book.Id).ToListAsync(cancellationToken))
        {
            (entry.Status, entry.Bookmark, entry.ModifiedAt) = (null, null, DateTimeOffset.UtcNow);
        }

        db.PositionReports.RemoveRange(await db.PositionReports.Where(item => item.ReaderId == readerId && item.BookId == book.Id).ToListAsync(cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
        return AgainResult.Started;
    }

    public static async Task<bool> UpdateAsync(ShelfDb db, int bookId, int readId, int? rating, string? thoughts, CancellationToken cancellationToken = default)
    {
        var read = await db.PastReads.FirstOrDefaultAsync(item => item.Id == readId && item.BookId == bookId, cancellationToken);
        thoughts = thoughts?.Trim();
        if (read is null || rating is < 1 or > 5 || thoughts?.Length > PastRead.MaxThoughtsLength)
        {
            return false;
        }

        read.Rating = rating;
        read.Thoughts = string.IsNullOrEmpty(thoughts) ? null : thoughts;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public static async Task<PastRead[]> ForBookAsync(ShelfDb db, int bookId, CancellationToken cancellationToken = default) =>
        // By the day each ended, or began when no end was kept; a read with neither comes after the dated ones.
        await db.PastReads.AsNoTracking().Where(read => read.BookId == bookId)
            .OrderBy(read => (read.FinishedOn ?? read.StartedOn) == null).ThenBy(read => read.FinishedOn ?? read.StartedOn).ThenBy(read => read.Id)
            .ToArrayAsync(cancellationToken);

    // Every read of the book, first to last; what was logged, highlighted, and quoted falls in the read whose days
    // it was made in. Null for a book not on the shelf; one read, the book's own, for a book never read again.
    public static async Task<ReadThrough[]?> ThroughsAsync(ShelfDb db, int bookId, CancellationToken cancellationToken = default)
    {
        var book = await db.Books.AsNoTracking().Where(item => item.Id == bookId)
            .Select(item => new { item.StartedOn, item.FinishedOn, item.Rating, item.Status })
            .FirstOrDefaultAsync(cancellationToken);
        if (book is null)
        {
            return null;
        }

        var past = await ForBookAsync(db, bookId, cancellationToken);
        var sessions = await db.Sessions.AsNoTracking().Where(item => item.BookId == bookId).Select(item => item.Date).ToListAsync(cancellationToken);
        var highlights = await db.Highlights.AsNoTracking().Where(item => item.BookId == bookId).Select(item => item.NotedAt).ToListAsync(cancellationToken);
        var quotes = await db.Quotes.AsNoTracking().Where(item => item.BookId == bookId).Select(item => item.NotedAt).ToListAsync(cancellationToken);
        return Throughs(past, book.StartedOn, book.FinishedOn, book.Rating, book.Status, sessions, highlights.Select(Day).ToList(), quotes.Select(Day).ToList());
    }

    // Each read runs from the day after the one before it ended to the day it finished; the last runs on.
    public static ReadThrough[] Throughs(
        IReadOnlyList<PastRead> past,
        DateOnly? startedOn,
        DateOnly? finishedOn,
        int? rating,
        BookStatus status,
        IReadOnlyList<DateOnly> sessions,
        IReadOnlyList<DateOnly> highlights,
        IReadOnlyList<DateOnly> quotes)
    {
        var reads = new List<ReadThrough>();
        DateOnly? after = null;
        for (var index = 0; index <= past.Count; index++)
        {
            var current = index == past.Count;
            if (current && past.Count > 0 && status == BookStatus.Want)
            {
                break;
            }

            // An earlier read kept with no end ends the day before the next began: open-ended, it and every read after
            // it each counted the same sessions, highlights, and quotes.
            var next = current ? null : index + 1 < past.Count ? past[index + 1].StartedOn : startedOn;
            var end = current ? (DateOnly?)null : past[index].FinishedOn ?? next?.AddDays(-1);
            bool Within(DateOnly day) => (after is null || day > after) && (end is null || day <= end);
            reads.Add(new ReadThrough(
                current ? null : past[index].Id,
                index + 1,
                current ? startedOn : past[index].StartedOn,
                current ? finishedOn : past[index].FinishedOn,
                current ? rating : past[index].Rating,
                current ? null : past[index].Thoughts,
                sessions.Count(Within),
                highlights.Count(Within),
                quotes.Count(Within),
                current));
            after = end ?? after;
        }

        return [.. reads];
    }

    // The year's finishes, earlier reads of books read again among them, for counting and listing a year.
    public static async Task<List<Finish>> FinishesAsync(ShelfDb db, int? year = null, CancellationToken cancellationToken = default)
    {
        var books = db.Books.AsNoTracking().Where(book => book.Status == BookStatus.Finished && book.FinishedOn != null);
        var again = db.PastReads.AsNoTracking().Where(read => read.FinishedOn != null);
        if (year is int only)
        {
            var (from, to) = (new DateOnly(only, 1, 1), new DateOnly(only, 12, 31));
            books = books.Where(book => book.FinishedOn >= from && book.FinishedOn <= to);
            again = again.Where(read => read.FinishedOn >= from && read.FinishedOn <= to);
        }

        var finishes = await books
            .Select(book => new Finish(book.Id, book.Title, book.Author, book.FinishedOn!.Value, book.Pages, book.Rating, false))
            .ToListAsync(cancellationToken);
        finishes.AddRange(await again
            .Select(read => new Finish(read.BookId, read.Book!.Title, read.Book.Author, read.FinishedOn!.Value, read.Book.Pages, read.Rating, true))
            .ToListAsync(cancellationToken));
        return finishes;
    }

    // How many earlier finishes fell in the year: to add to the books whose own finish did.
    public static async Task<int> EarlierFinishesAsync(ShelfDb db, int year, CancellationToken cancellationToken = default)
    {
        var (from, to) = (new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
        return await db.PastReads.AsNoTracking().CountAsync(read => read.FinishedOn >= from && read.FinishedOn <= to, cancellationToken);
    }

    // The book as it was when an earlier read of it ended.
    public static Book Earlier(Book book, PastRead read) => new()
    {
        Id = book.Id,
        Title = book.Title,
        Author = book.Author,
        Pages = book.Pages,
        Format = book.Format,
        CoverUrl = book.CoverUrl,
        FileCover = book.FileCover,
        CoverImage = book.CoverImage,
        Tags = book.Tags,
        Status = BookStatus.Finished,
        StartedOn = read.StartedOn,
        FinishedOn = read.FinishedOn,
        Rating = read.Rating,
    };

    // Every earlier finish, as the book was then, to count beside the books' own.
    public static async Task<List<Book>> EarlierBooksAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        (await db.PastReads.AsNoTracking().Where(read => read.FinishedOn != null).Include(read => read.Book).ToListAsync(cancellationToken))
            .Select(read => Earlier(read.Book!, read)).ToList();

    private static DateOnly Day(DateTimeOffset at) => DateOnly.FromDateTime(at.UtcDateTime);
}

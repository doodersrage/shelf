using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public sealed record QueuedBook(int Id, string Title, string Author, string? Series, int? SeriesNumber, string? Cover, DateOnly? HeldUntil, string? HoldNote, bool Held, string? Waiting);

public sealed record QueueView(QueuedBook[] Ready, QueuedBook[] Held);

public sealed record HoldRequest(DateOnly? Until, string? Note);

public sealed record QueueOrderRequest(int[] BookIds);

// The few books a reader means to start soon, in their order: short, unlike the whole want-to-read list. A book can
// wait for a day or a mood ("for the winter", "on holiday") and keeps its place. A book in a series waits for the one
// before it, when that one is on the shelf and unread, without being held.
public static class ReadingQueue
{
    public const int MaxQueued = 100;

    public static void Map(RouteGroupBuilder books)
    {
        var queue = books.MapGroup("/queue").WithTags("Your reading");
        queue.MapGet("/", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await ViewAsync(db, cancellationToken: cancellationToken)));
        queue.MapPut("/", async (QueueOrderRequest request, ShelfDb db, CancellationToken cancellationToken) =>
        {
            await OrderAsync(db, request.BookIds ?? [], cancellationToken);
            return TypedResults.NoContent();
        });
        queue.MapPost("/{id:int}", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await AddAsync(db, id, cancellationToken) switch
            {
                null => TypedResults.NoContent(),
                var problem when problem == Missing => TypedResults.NotFound(),
                var problem => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Id"] = [problem] }),
            });
        queue.MapDelete("/{id:int}", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await RemoveAsync(db, id, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());
        queue.MapPut("/{id:int}/hold", async Task<IResult> (int id, HoldRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            await HoldAsync(db, id, request.Until, request.Note, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());
        queue.MapDelete("/{id:int}/hold", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await HoldAsync(db, id, null, null, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());
    }

    private static readonly string Missing = "\0";

    public static async Task<QueueView> ViewAsync(ShelfDb db, DateOnly? today = null, CancellationToken cancellationToken = default)
    {
        var now = today ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var queued = await db.Books.AsNoTracking().Where(book => book.Queued && book.Status == BookStatus.Want && book.LeftOn == null)
            .OrderBy(book => book.QueueOrder == null).ThenBy(book => book.QueueOrder).ThenBy(book => book.Id).ToListAsync(cancellationToken);
        var series = queued.Where(book => book.Series is not null && book.SeriesNumber is not null).Select(book => book.Series!).Distinct().ToList();
        var earlier = series.Count == 0 ? [] : await db.Books.AsNoTracking()
            .Where(book => book.Series != null && series.Contains(book.Series) && book.SeriesNumber != null && book.LeftOn == null && book.Status != BookStatus.Finished)
            .Select(book => new { book.Id, Series = book.Series!, Number = book.SeriesNumber!.Value, book.Title }).ToListAsync(cancellationToken);
        QueuedBook Show(Book book)
        {
            var held = book.HeldUntil is DateOnly until ? until > now : book.HoldNote is not null;
            var before = book.Series is { } name && book.SeriesNumber is int number
                ? earlier.Where(item => item.Id != book.Id && item.Series == name && item.Number < number).MaxBy(item => item.Number)
                : null;
            return new QueuedBook(book.Id, book.Title, book.Author, book.Series, book.SeriesNumber, book.CoverShown, book.HeldUntil, book.HoldNote, held,
                before is null ? null : T("after {0}", before.Title));
        }

        var all = queued.Select(Show).ToList();
        return new QueueView([.. all.Where(book => !book.Held)], [.. all.Where(book => book.Held)]);
    }

    // What to start after a book is finished: the next of its series on the shelf, then the queue's first ready.
    public static async Task<List<Book>> UpNextAsync(ShelfDb db, Book finished, int most = 3, CancellationToken cancellationToken = default)
    {
        var chosen = new List<Book>();
        if (finished.Series is { } name && finished.SeriesNumber is int number)
        {
            var next = await db.Books.AsNoTracking().Where(book => book.Series == name && book.SeriesNumber > number && book.Status == BookStatus.Want && book.LeftOn == null)
                .OrderBy(book => book.SeriesNumber).FirstOrDefaultAsync(cancellationToken);
            if (next is not null)
            {
                chosen.Add(next);
            }
        }

        var view = await ViewAsync(db, cancellationToken: cancellationToken);
        var ready = view.Ready.Where(book => book.Waiting is null && book.Id != finished.Id && chosen.All(item => item.Id != book.Id)).Take(most - chosen.Count).Select(book => book.Id).ToList();
        if (ready.Count > 0)
        {
            var books = await db.Books.AsNoTracking().Where(book => ready.Contains(book.Id)).ToListAsync(cancellationToken);
            chosen.AddRange(ready.Select(id => books.First(book => book.Id == id)));
        }

        return chosen;
    }

    // The order the reader gave; books left out keep theirs after.
    public static async Task OrderAsync(ShelfDb db, IReadOnlyList<int> bookIds, CancellationToken cancellationToken = default)
    {
        var queued = await db.Books.Where(book => book.Queued).ToListAsync(cancellationToken);
        var position = 0;
        foreach (var id in bookIds.Distinct())
        {
            if (queued.FirstOrDefault(book => book.Id == id) is { } book)
            {
                book.QueueOrder = ++position;
            }
        }

        foreach (var book in queued.Where(book => !bookIds.Contains(book.Id)).OrderBy(book => book.QueueOrder ?? int.MaxValue).ThenBy(book => book.AddedAt))
        {
            book.QueueOrder = ++position;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task<string?> AddAsync(ShelfDb db, int id, CancellationToken cancellationToken = default)
    {
        var book = await db.Books.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (book is null)
        {
            return Missing;
        }

        if (book.Status != BookStatus.Want || book.LeftOn is not null)
        {
            return T("Only a book you want to read can wait in the queue.");
        }

        if (!book.Queued && await db.Books.CountAsync(item => item.Queued, cancellationToken) >= MaxQueued)
        {
            return T("The queue holds up to {0} books; it's meant for the few you'll start soon.", MaxQueued);
        }

        book.Queued = true;
        book.QueueOrder ??= (await db.Books.Where(item => item.Queued && item.QueueOrder != null).MaxAsync(item => (int?)item.QueueOrder, cancellationToken) ?? 0) + 1;
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    public static async Task<bool> RemoveAsync(ShelfDb db, int id, CancellationToken cancellationToken = default)
    {
        var book = await db.Books.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (book is null)
        {
            return false;
        }

        (book.Queued, book.QueueOrder, book.HeldUntil, book.HoldNote) = (false, null, null, null);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Held until a day, or for a mood until let go; neither, and it is ready again.
    public static async Task<bool> HoldAsync(ShelfDb db, int id, DateOnly? until, string? note, CancellationToken cancellationToken = default)
    {
        var book = await db.Books.FirstOrDefaultAsync(item => item.Id == id && item.Queued, cancellationToken);
        if (book is null)
        {
            return false;
        }

        var text = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        (book.HeldUntil, book.HoldNote) = (until, text is { Length: > 80 } ? text[..80] : text);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

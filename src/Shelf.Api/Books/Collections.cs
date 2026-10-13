using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// A list a reader makes by hand, in an order of their choosing: a book club's year, summer reading, books to lend
// a friend. Unlike a saved search it holds exactly the books put in it. It can be shared by link, like a search.
public sealed class Collection
{
    public const int MaxNameLength = 80;
    public const int MaxDescriptionLength = 1000;
    public const int MaxBooks = 1000;

    public int Id { get; set; }
    public int OwnerId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? ShareToken { get; set; }

    // Shared with the owner's children's accounts, who may read its books.
    public bool ForChildren { get; set; }

    // A plan: the day the reader means to have read the books by.
    public DateOnly? ReadBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<CollectionBook> Books { get; set; } = [];
}

public sealed class CollectionBook
{
    public int CollectionId { get; set; }
    public Collection? Collection { get; set; }
    public int BookId { get; set; }
    public Book? Book { get; set; }
    public const int MaxNoteLength = 500;

    public int Position { get; set; }

    // Why the book is in the list, or what to look for in it; shown wherever the list is.
    public string? Note { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

public sealed record CollectionSummary(int Id, string Name, string? Description, int Books, bool Shared, string[] Covers);

// Notes runs beside Books: the note on each book in the list, or null.
public sealed record CollectionResponse(int Id, string Name, string? Description, string? ShareToken, BookResponse[] Books, DateOnly? ReadBy = null, string?[]? Notes = null);

public sealed record CollectionRequest([Required, MaxLength(Collection.MaxNameLength)] string Name, [MaxLength(Collection.MaxDescriptionLength)] string? Description = null);

public sealed record CollectionBookRequest(int BookId);

public sealed record CollectionOrderRequest(int[] BookIds);

public sealed record CollectionNoteRequest([MaxLength(CollectionBook.MaxNoteLength)] string? Note);

public sealed record CollectionPlanRequest(DateOnly? ReadBy);

// Whether a plan fits, by the reader's own pace: pages a day over the last year's finished books. Books with no page
// count are taken at the reader's usual length. Behind, it says what would fit: a later day, or fewer books.
public sealed record PlanEstimate(
    DateOnly ReadBy,
    int BooksLeft,
    int PagesLeft,
    int Guessed,
    int PagesPerDay,
    int DaysNeeded,
    int DaysLeft,
    DateOnly FitsBy,
    int LeaveOut)
{
    public bool Fits => DaysNeeded <= DaysLeft;
}

public static class Collections
{
    public static void Map(RouteGroupBuilder books)
    {
        var collections = books.MapGroup("/collections");
        collections.MapGet("/", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await ListAsync(db, cancellationToken)));
        collections.MapPost("/", Create);
        collections.MapGet("/{id:int}", Get);
        collections.MapPut("/{id:int}", Update);
        collections.MapDelete("/{id:int}", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await DeleteAsync(db, id, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());
        collections.MapPost("/{id:int}/books", async Task<IResult> (int id, CollectionBookRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            await AddAsync(db, id, request.BookId, cancellationToken) switch
            {
                AddResult.Added or AddResult.AlreadyThere => TypedResults.NoContent(),
                AddResult.Full => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["BookId"] = [T("A collection holds up to {0} books.", Collection.MaxBooks)] }),
                _ => TypedResults.NotFound(),
            });
        collections.MapDelete("/{id:int}/books/{bookId:int}", async Task<IResult> (int id, int bookId, ShelfDb db, CancellationToken cancellationToken) =>
            await RemoveAsync(db, id, bookId, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());
        collections.MapPut("/{id:int}/order", async Task<IResult> (int id, CollectionOrderRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            await OrderAsync(db, id, request.BookIds, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());
        collections.MapPut("/{id:int}/books/{bookId:int}", async Task<IResult> (int id, int bookId, CollectionNoteRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            await NoteAsync(db, id, bookId, request.Note, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());
        collections.MapPut("/{id:int}/plan", async Task<IResult> (int id, CollectionPlanRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            await PlanAsync(db, id, request.ReadBy, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());
        collections.MapGet("/{id:int}/plan", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            !await db.Collections.AnyAsync(item => item.Id == id, cancellationToken) ? TypedResults.NotFound()
            : await EstimateAsync(db, id, cancellationToken) is { } estimate ? TypedResults.Ok(estimate) : TypedResults.NoContent());
    }

    public static async Task<CollectionSummary[]> ListAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var collections = await db.Collections.AsNoTracking()
            .Select(collection => new { collection.Id, collection.Name, collection.Description, Shared = collection.ShareToken != null })
            .ToListAsync(cancellationToken);
        var entries = await db.CollectionBooks.AsNoTracking()
            .Select(entry => new { entry.CollectionId, entry.Position, entry.Book!.Id, entry.Book.CoverUrl, entry.Book.FileCover, entry.Book.CoverImage })
            .ToListAsync(cancellationToken);
        return collections
            .OrderBy(collection => collection.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(collection =>
            {
                var books = entries.Where(entry => entry.CollectionId == collection.Id).OrderBy(entry => entry.Position).ToList();
                return new CollectionSummary(
                    collection.Id,
                    collection.Name,
                    collection.Description,
                    books.Count,
                    collection.Shared,
                    books.Select(book => Covers.For(book.Id, book.CoverUrl, book.FileCover, book.CoverImage)).OfType<string>().Take(4).ToArray());
            })
            .ToArray();
    }

    public static async Task<Collection?> FindAsync(ShelfDb db, int id, CancellationToken cancellationToken = default) =>
        await db.Collections.AsNoTracking()
            .Include(collection => collection.Books.OrderBy(entry => entry.Position))
            .ThenInclude(entry => entry.Book)
            .AsSplitQuery()
            .FirstOrDefaultAsync(collection => collection.Id == id, cancellationToken);

    // The collections a book is in, and those it could go into.
    public static async Task<(int Id, string Name, bool Holds)[]> ForBookAsync(ShelfDb db, int bookId, CancellationToken cancellationToken = default) =>
        (await db.Collections.AsNoTracking()
            .Select(collection => new { collection.Id, collection.Name, Holds = collection.Books.Any(entry => entry.BookId == bookId) })
            .ToListAsync(cancellationToken))
            .OrderBy(collection => collection.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(collection => (collection.Id, collection.Name, collection.Holds))
            .ToArray();

    public static async Task<(Collection? Made, string? Problem)> CreateAsync(ShelfDb db, string? name, string? description, CancellationToken cancellationToken = default)
    {
        if (Problem(name, description) is { } problem)
        {
            return (null, problem);
        }

        var collection = new Collection { OwnerId = db.ReaderId, Name = name!.Trim(), Description = Blank(description), CreatedAt = DateTimeOffset.UtcNow };
        db.Collections.Add(collection);
        await db.SaveChangesAsync(cancellationToken);
        return (collection, null);
    }

    public static async Task<string?> UpdateAsync(ShelfDb db, int id, string? name, string? description, CancellationToken cancellationToken = default)
    {
        if (Problem(name, description) is { } problem)
        {
            return problem;
        }

        var collection = await db.Collections.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (collection is null)
        {
            return T("That collection is not on your shelf.");
        }

        collection.Name = name!.Trim();
        collection.Description = Blank(description);
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    public static async Task<bool> DeleteAsync(ShelfDb db, int id, CancellationToken cancellationToken = default) =>
        await db.Collections.Where(item => item.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;

    public enum AddResult
    {
        Added,
        AlreadyThere,
        Full,
        NotFound,
    }

    // A book goes at the end. Only the reader's own books go into their collections.
    public static async Task<AddResult> AddAsync(ShelfDb db, int id, int bookId, CancellationToken cancellationToken = default)
    {
        if (!await db.Collections.AnyAsync(item => item.Id == id, cancellationToken) || !await db.Books.AnyAsync(book => book.Id == bookId, cancellationToken))
        {
            return AddResult.NotFound;
        }

        var positions = await db.CollectionBooks.Where(entry => entry.CollectionId == id).Select(entry => new { entry.BookId, entry.Position }).ToListAsync(cancellationToken);
        if (positions.Any(entry => entry.BookId == bookId))
        {
            return AddResult.AlreadyThere;
        }

        if (positions.Count >= Collection.MaxBooks)
        {
            return AddResult.Full;
        }

        db.CollectionBooks.Add(new CollectionBook { CollectionId = id, BookId = bookId, Position = positions.Count == 0 ? 0 : positions.Max(entry => entry.Position) + 1, AddedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
        return AddResult.Added;
    }

    public static async Task<bool> RemoveAsync(ShelfDb db, int id, int bookId, CancellationToken cancellationToken = default) =>
        await db.CollectionBooks.Where(entry => entry.CollectionId == id && entry.BookId == bookId && entry.Collection!.OwnerId == db.ReaderId)
            .ExecuteDeleteAsync(cancellationToken) > 0;

    // A new order: the books named come first, in that order, and any left out keep theirs after them.
    public static async Task<bool> OrderAsync(ShelfDb db, int id, IReadOnlyList<int> bookIds, CancellationToken cancellationToken = default)
    {
        if (!await db.Collections.AnyAsync(item => item.Id == id, cancellationToken))
        {
            return false;
        }

        var entries = await db.CollectionBooks.Where(entry => entry.CollectionId == id).OrderBy(entry => entry.Position).ToListAsync(cancellationToken);
        var wanted = bookIds.Distinct().ToList();
        var ordered = wanted.Select(bookId => entries.FirstOrDefault(entry => entry.BookId == bookId)).OfType<CollectionBook>()
            .Concat(entries.Where(entry => !wanted.Contains(entry.BookId)))
            .ToList();
        for (var position = 0; position < ordered.Count; position++)
        {
            ordered[position].Position = position;
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Moves one book a place up (-1) or down (1).
    public static async Task<bool> MoveAsync(ShelfDb db, int id, int bookId, int direction, CancellationToken cancellationToken = default)
    {
        var order = await db.CollectionBooks.Where(entry => entry.CollectionId == id && entry.Collection!.OwnerId == db.ReaderId)
            .OrderBy(entry => entry.Position).Select(entry => entry.BookId).ToListAsync(cancellationToken);
        var at = order.IndexOf(bookId);
        var to = at + Math.Sign(direction);
        if (at < 0 || to < 0 || to >= order.Count)
        {
            return false;
        }

        (order[at], order[to]) = (order[to], order[at]);
        return await OrderAsync(db, id, order, cancellationToken);
    }

    // Why a book is in the list; blank takes the note away.
    public static async Task<bool> NoteAsync(ShelfDb db, int id, int bookId, string? note, CancellationToken cancellationToken = default)
    {
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        var entry = await db.CollectionBooks.FirstOrDefaultAsync(item => item.CollectionId == id && item.BookId == bookId, cancellationToken);
        if (entry is null || note?.Length > CollectionBook.MaxNoteLength)
        {
            return false;
        }

        entry.Note = note;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // The day to have read the books by; none makes the list a list again.
    public static async Task<bool> PlanAsync(ShelfDb db, int id, DateOnly? readBy, CancellationToken cancellationToken = default)
    {
        var collection = await db.Collections.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (collection is null)
        {
            return false;
        }

        collection.ReadBy = readBy;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public const int UsualPages = 300;

    // Null for a list with no day to read by, or nothing left to read in it.
    public static async Task<PlanEstimate?> EstimateAsync(ShelfDb db, int id, CancellationToken cancellationToken = default, DateOnly? today = null)
    {
        var readBy = await db.Collections.AsNoTracking().Where(item => item.Id == id).Select(item => item.ReadBy).FirstOrDefaultAsync(cancellationToken);
        if (readBy is not DateOnly by)
        {
            return null;
        }

        var left = await db.CollectionBooks.AsNoTracking()
            .Where(entry => entry.CollectionId == id && entry.Book!.Status != BookStatus.Finished && entry.Book.Status != BookStatus.Abandoned)
            .Select(entry => new { entry.Book!.Pages, entry.Book.CurrentPage, entry.Book.Status })
            .ToListAsync(cancellationToken);
        if (left.Count == 0)
        {
            return null;
        }

        var now = today ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var yearAgo = now.AddDays(-365);
        var finished = await db.Books.AsNoTracking()
            .Where(book => book.Status == BookStatus.Finished && book.FinishedOn >= yearAgo && book.Pages > 0)
            .Select(book => book.Pages!.Value)
            .ToListAsync(cancellationToken);
        var usual = finished.Count == 0 ? UsualPages : finished.Order().ElementAt(finished.Count / 2);
        var pace = Math.Max(1, finished.Sum() / 365);
        var lengths = left.Select(book => book.Pages is int pages ? Math.Max(0, pages - (book.Status == BookStatus.Reading ? book.CurrentPage ?? 0 : 0)) : usual).ToList();
        var pagesLeft = lengths.Sum();
        var needed = (pagesLeft + pace - 1) / pace;
        var daysLeft = Math.Max(0, by.DayNumber - now.DayNumber);

        // Fewest books to leave out to fit: the longest first.
        var leaveOut = 0;
        var fitting = pagesLeft;
        foreach (var length in lengths.OrderDescending())
        {
            if ((fitting + pace - 1) / pace <= daysLeft)
            {
                break;
            }

            fitting -= length;
            leaveOut++;
        }

        return new PlanEstimate(by, left.Count, pagesLeft, left.Count(book => book.Pages is null), pace, needed, daysLeft, now.AddDays(needed), leaveOut);
    }

    public static async Task<string?> ShareAsync(ShelfDb db, int id, CancellationToken cancellationToken = default)
    {
        var collection = await db.Collections.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (collection is null || await Family.IsRestrictedAsync(db, cancellationToken: cancellationToken))
        {
            return null;
        }

        collection.ShareToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(18));
        await db.SaveChangesAsync(cancellationToken);
        await Audit.NoteAsync(db, Say("Shared a reading list by link"), detail: collection.Name, cancellationToken: cancellationToken);
        return collection.ShareToken;
    }

    public static async Task StopSharingAsync(ShelfDb db, int id, CancellationToken cancellationToken = default)
    {
        var collection = await db.Collections.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (collection?.ShareToken is null)
        {
            return;
        }

        collection.ShareToken = null;
        await db.SaveChangesAsync(cancellationToken);
        await Audit.NoteAsync(db, Say("Stopped sharing a reading list"), detail: collection.Name, cancellationToken: cancellationToken);
    }

    private static string? Problem(string? name, string? description)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return T("Give the collection a name.");
        }

        if (name.Trim().Length > Collection.MaxNameLength)
        {
            return T("Keep the name to {0} characters.", Collection.MaxNameLength);
        }

        return description?.Trim().Length > Collection.MaxDescriptionLength ? T("Keep the description to {0} characters.", Collection.MaxDescriptionLength) : null;
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static async Task<IResult> Create(CollectionRequest request, ShelfDb db, CancellationToken cancellationToken)
    {
        var (made, problem) = await CreateAsync(db, request.Name, request.Description, cancellationToken);
        return made is null
            ? TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Name"] = [problem!] })
            : TypedResults.Created($"/books/collections/{made.Id}", new CollectionResponse(made.Id, made.Name, made.Description, null, []));
    }

    private static async Task<Results<Ok<CollectionResponse>, NotFound>> Get(int id, ShelfDb db, CancellationToken cancellationToken)
    {
        if (await FindAsync(db, id, cancellationToken) is not { } collection)
        {
            return TypedResults.NotFound();
        }

        var ids = collection.Books.Select(entry => entry.BookId).ToList();
        var books = await db.Books.AsNoTracking().WithDetails().Where(book => ids.Contains(book.Id)).ToListAsync(cancellationToken);
        var listed = collection.Books.Where(entry => books.Any(book => book.Id == entry.BookId)).ToList();
        return TypedResults.Ok(new CollectionResponse(
            collection.Id,
            collection.Name,
            collection.Description,
            collection.ShareToken,
            listed.Select(entry => BookResponse.From(books.First(book => book.Id == entry.BookId))).ToArray(),
            collection.ReadBy,
            listed.Select(entry => entry.Note).ToArray()));
    }

    private static async Task<IResult> Update(int id, CollectionRequest request, ShelfDb db, CancellationToken cancellationToken)
    {
        if (!await db.Collections.AnyAsync(item => item.Id == id, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        return await UpdateAsync(db, id, request.Name, request.Description, cancellationToken) is { } problem
            ? TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Name"] = [problem] })
            : TypedResults.NoContent();
    }
}

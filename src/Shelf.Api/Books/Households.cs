using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// A household library, as Audiobookshelf shares a library with chosen users: a collection its owner shares with
// readers they pick, who read and listen to its books as if lent, each with their own place, highlights, and
// bookmarks, and none of the owner's. Each member may or may not download files, and may have books tagged
// "explicit" kept from them. Only the owner changes the books; a member never sees the owner's notes, review, or
// reading, nor another member's.
public sealed class CollectionMember
{
    public const string ExplicitTag = "explicit";

    public int Id { get; set; }
    public int CollectionId { get; set; }
    public Collection? Collection { get; set; }
    public int ReaderId { get; set; }
    public bool CanDownload { get; set; } = true;
    public bool HideExplicit { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

public sealed record HouseholdMember(int Id, int ReaderId, string Name, bool CanDownload, bool HideExplicit);

public sealed record HouseholdLibrary(int Id, string Name, string Owner, FamilyBook[] Books);

public sealed record MemberRequest(int ReaderId, bool CanDownload = true, bool HideExplicit = false);

public sealed record MemberChange(bool CanDownload, bool HideExplicit);

public static class Households
{
    public static void Map(RouteGroupBuilder books)
    {
        books.MapGet("/households", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await LibrariesAsync(db, cancellationToken))).WithTags("Sharing");
        books.MapGet("/collections/{id:int}/members", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await MembersAsync(db, id, cancellationToken) is { } members ? TypedResults.Ok(members) : TypedResults.NotFound()).WithTags("Sharing");
        books.MapPost("/collections/{id:int}/members", async Task<IResult> (int id, MemberRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            await AddAsync(db, id, request.ReaderId, request.CanDownload, request.HideExplicit, cancellationToken) switch
            {
                null => TypedResults.NoContent(),
                "" => TypedResults.NotFound(),
                var problem => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["ReaderId"] = [problem] }),
            }).WithTags("Sharing");
        books.MapPut("/collections/{id:int}/members/{memberId:int}", async Task<IResult> (int id, int memberId, MemberChange change, ShelfDb db, CancellationToken cancellationToken) =>
            await ChangeAsync(db, id, memberId, change.CanDownload, change.HideExplicit, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound()).WithTags("Sharing");
        books.MapDelete("/collections/{id:int}/members/{memberId:int}", async Task<IResult> (int id, int memberId, ShelfDb db, CancellationToken cancellationToken) =>
            await RemoveAsync(db, id, memberId, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound()).WithTags("Sharing");
    }

    // The memberships that open a book to this reader: in a collection shared with them, its owner still on the
    // shelf and not suspended, and the book the owner's own.
    private static IQueryable<CollectionMember> Opening(ShelfDb db, int bookId)
    {
        var readerId = db.ReaderId;
        return db.CollectionMembers.IgnoreQueryFilters().Where(member => readerId != 0 && member.ReaderId == readerId
            && member.Collection!.Books.Any(entry => entry.BookId == bookId && entry.Book!.OwnerId == member.Collection.OwnerId)
            && !db.Readers.Any(owner => owner.Id == member.Collection!.OwnerId && owner.Suspended));
    }

    // The book, when a household library opens it to this reader; never for a child's account, and never one tagged
    // explicit for a member who has that kept from them.
    public static async Task<Book?> SharedBookAsync(ShelfDb db, int bookId, CancellationToken cancellationToken = default)
    {
        var memberships = await Opening(db, bookId).Select(member => new { member.HideExplicit }).ToListAsync(cancellationToken);
        if (memberships.Count == 0 || await Family.IsRestrictedAsync(db, cancellationToken: cancellationToken))
        {
            return null;
        }

        var book = await db.Books.IgnoreQueryFilters().AsNoTracking().Include(item => item.Tags).FirstOrDefaultAsync(item => item.Id == bookId, cancellationToken);
        var isExplicit = book?.Tags.Any(tag => tag.Name == CollectionMember.ExplicitTag) == true;
        return book is not null && memberships.Any(member => !member.HideExplicit || !isExplicit) ? book : null;
    }

    // Whether this reader may download the book's files: always their own and a book lent to them; a household
    // book when any membership opening it says so.
    public static async Task<bool> MayDownloadAsync(ShelfDb db, OpenBook open, CancellationToken cancellationToken = default)
    {
        if (!open.Household)
        {
            return true;
        }

        // A membership that keeps the book from them gives no download of it.
        var isExplicit = await db.Books.IgnoreQueryFilters().AnyAsync(book => book.Id == open.Book.Id && book.Tags.Any(tag => tag.Name == CollectionMember.ExplicitTag), cancellationToken);
        return await Opening(db, open.Book.Id).AnyAsync(member => member.CanDownload && !(member.HideExplicit && isExplicit), cancellationToken);
    }

    // The household books this reader may download, by id, in one look: for the e-reader catalog, which hands out files.
    public static async Task<HashSet<int>> DownloadableIdsAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var readerId = db.ReaderId;
        if (readerId == 0 || await Family.IsRestrictedAsync(db, cancellationToken: cancellationToken))
        {
            return [];
        }

        // Flat, over the collections' books, as SQLite takes it.
        return (await db.CollectionBooks.IgnoreQueryFilters().AsNoTracking()
            .Where(entry => entry.Book!.OwnerId == entry.Collection!.OwnerId
                && !db.Readers.Any(owner => owner.Id == entry.Collection.OwnerId && owner.Suspended)
                && db.CollectionMembers.IgnoreQueryFilters().Any(member => member.CollectionId == entry.CollectionId && member.ReaderId == readerId && member.CanDownload
                    && (!member.HideExplicit || !entry.Book.Tags.Any(tag => tag.Name == CollectionMember.ExplicitTag))))
            .Select(entry => entry.BookId)
            .ToListAsync(cancellationToken)).ToHashSet();
    }

    // The household libraries shared with this reader, with the books each opens to them.
    public static async Task<HouseholdLibrary[]> LibrariesAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var readerId = db.ReaderId;
        if (readerId == 0 || await Family.IsRestrictedAsync(db, cancellationToken: cancellationToken))
        {
            return [];
        }

        var memberships = await db.CollectionMembers.IgnoreQueryFilters().AsNoTracking()
            .Where(member => member.ReaderId == readerId && !db.Readers.Any(owner => owner.Id == member.Collection!.OwnerId && owner.Suspended))
            .Select(member => new { member.CollectionId, member.HideExplicit, member.Collection!.Name, member.Collection.OwnerId, Owner = db.Readers.Where(owner => owner.Id == member.Collection.OwnerId).Select(owner => owner.Name).FirstOrDefault() })
            .ToListAsync(cancellationToken);
        var libraries = new List<HouseholdLibrary>();
        foreach (var membership in memberships.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var rows = await db.CollectionBooks.IgnoreQueryFilters().AsNoTracking()
                .Where(entry => entry.CollectionId == membership.CollectionId && entry.Book!.OwnerId == membership.OwnerId
                    && (!membership.HideExplicit || !entry.Book.Tags.Any(tag => tag.Name == CollectionMember.ExplicitTag)))
                .OrderBy(entry => entry.Position)
                .Select(entry => new { entry.Book!.Id, entry.Book.Title, entry.Book.Author, entry.Book.CoverUrl, entry.Book.FileCover, entry.Book.CoverImage, entry.Book.EbookStoredName, entry.Book.AudioStoredName })
                .ToListAsync(cancellationToken);
            libraries.Add(new HouseholdLibrary(membership.CollectionId, membership.Name, membership.Owner ?? "", [.. rows.Select(row => new FamilyBook(row.Id, row.Title, row.Author,
                Covers.For(row.Id, row.CoverUrl, row.FileCover, row.CoverImage), row.EbookStoredName is not null, row.AudioStoredName is not null, membership.Name))]));
        }

        return [.. libraries];
    }

    // Who a collection of the reader's own is shared with; null when it isn't theirs.
    public static async Task<HouseholdMember[]?> MembersAsync(ShelfDb db, int collectionId, CancellationToken cancellationToken = default)
    {
        if (!await db.Collections.AnyAsync(item => item.Id == collectionId, cancellationToken))
        {
            return null;
        }

        var members = await db.CollectionMembers.IgnoreQueryFilters().AsNoTracking().Where(member => member.CollectionId == collectionId)
            .Select(member => new { member.Id, member.ReaderId, member.CanDownload, member.HideExplicit, Name = db.Readers.Where(reader => reader.Id == member.ReaderId).Select(reader => reader.Name).FirstOrDefault() })
            .ToListAsync(cancellationToken);
        return [.. members.OrderBy(member => member.Name, StringComparer.CurrentCultureIgnoreCase).Select(member => new HouseholdMember(member.Id, member.ReaderId, member.Name ?? "", member.CanDownload, member.HideExplicit))];
    }

    // Null when added; empty when the collection isn't the reader's; else why not.
    public static async Task<string?> AddAsync(ShelfDb db, int collectionId, int readerId, bool canDownload, bool hideExplicit, CancellationToken cancellationToken = default)
    {
        if (!await db.Collections.AnyAsync(item => item.Id == collectionId, cancellationToken))
        {
            return "";
        }

        if (await Family.IsRestrictedAsync(db, cancellationToken: cancellationToken))
        {
            return T("A child's account cannot do that. Ask the grown-up who made it.");
        }

        if (readerId == db.ReaderId)
        {
            return T("The collection is yours already.");
        }

        // One answer for anyone who can't be chosen, so it tells nothing of who is suspended or a child.
        var reader = await db.Readers.AsNoTracking().FirstOrDefaultAsync(item => item.Id == readerId, cancellationToken);
        if (reader is null || reader.Suspended || reader.Restricted)
        {
            return T("Choose a reader on this shelf.");
        }

        if (await db.CollectionMembers.IgnoreQueryFilters().AnyAsync(member => member.CollectionId == collectionId && member.ReaderId == readerId, cancellationToken))
        {
            return T("It's shared with them already.");
        }

        db.CollectionMembers.Add(new CollectionMember { CollectionId = collectionId, ReaderId = readerId, CanDownload = canDownload, HideExplicit = hideExplicit, AddedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    public static async Task<bool> ChangeAsync(ShelfDb db, int collectionId, int memberId, bool canDownload, bool hideExplicit, CancellationToken cancellationToken = default) =>
        await db.Collections.AnyAsync(item => item.Id == collectionId, cancellationToken)
        && await db.CollectionMembers.IgnoreQueryFilters().Where(member => member.Id == memberId && member.CollectionId == collectionId)
            .ExecuteUpdateAsync(set => set.SetProperty(member => member.CanDownload, canDownload).SetProperty(member => member.HideExplicit, hideExplicit), cancellationToken) > 0;

    // A member let go: the books close to them at once. What they wrote in them stays theirs, out of sight, and
    // comes back if they're let in again.
    public static async Task<bool> RemoveAsync(ShelfDb db, int collectionId, int memberId, CancellationToken cancellationToken = default) =>
        await db.Collections.AnyAsync(item => item.Id == collectionId, cancellationToken)
        && await db.CollectionMembers.IgnoreQueryFilters().Where(member => member.Id == memberId && member.CollectionId == collectionId).ExecuteDeleteAsync(cancellationToken) > 0;
}

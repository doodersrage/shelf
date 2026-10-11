using Microsoft.EntityFrameworkCore;
using Shelf.Api.Books;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Readers;

public sealed record ChildAccount(int Id, string Name, DateTimeOffset CreatedAt, int Books);

// A book a child may read from their guardian's shelf: in a collection the guardian shares with their children.
public sealed record FamilyBook(int Id, string Title, string Author, string? Cover, bool HasEbook, bool HasAudio, string Collection);

// Children's accounts. A reader can make accounts for their children, who sign in as themselves, keep a shelf of
// their own, and read the books in the collections their guardian shares with them, each with their own place and
// highlights. A child's account cannot lend, borrow from other readers, open its shelf, share links, join clubs
// other than by their guardian, make API tokens or webhooks, or connect other services; and is never an admin.
public static class Family
{
    public const int MaxChildren = 10;

    public static async Task<bool> IsRestrictedAsync(ShelfDb db, int? readerId = null, CancellationToken cancellationToken = default)
    {
        var id = readerId ?? db.ReaderId;
        return id != 0 && await db.Readers.AsNoTracking().AnyAsync(reader => reader.Id == id && reader.Restricted, cancellationToken);
    }

    public static async Task<int?> GuardianOfAsync(ShelfDb db, int readerId, CancellationToken cancellationToken = default) =>
        await db.Readers.AsNoTracking().Where(reader => reader.Id == readerId && reader.Restricted).Select(reader => reader.GuardianId).FirstOrDefaultAsync(cancellationToken);

    public static async Task<(Reader? Child, AccountProblem? Problem)> CreateChildAsync(ShelfDb db, string? name, string? password, CancellationToken cancellationToken = default)
    {
        var me = db.ReaderId;
        if (await IsRestrictedAsync(db, me, cancellationToken))
        {
            return (null, AccountProblem.NotAllowed);
        }

        if (await db.Readers.CountAsync(reader => reader.GuardianId == me, cancellationToken) >= MaxChildren)
        {
            return (null, AccountProblem.TooManyChildren);
        }

        var (child, problem) = await ReaderRules.CreateAsync(db, name, password, cancellationToken);
        if (child is null)
        {
            return (null, problem);
        }

        (child.Restricted, child.GuardianId, child.Language) = (true, me, await db.Readers.Where(reader => reader.Id == me).Select(reader => reader.Language).FirstOrDefaultAsync(cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
        await Audit.NoteAsync(db, Say("Made a child's account"), await db.Readers.FirstAsync(reader => reader.Id == me, cancellationToken), child.Name, cancellationToken);
        return (child, null);
    }

    public static async Task<ChildAccount[]> ChildrenAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var me = db.ReaderId;
        var children = await db.Readers.AsNoTracking().Where(reader => reader.GuardianId == me && reader.Restricted).ToListAsync(cancellationToken);
        var ids = children.Select(child => child.Id).ToList();
        var counts = await db.Books.IgnoreQueryFilters().Where(book => book.OwnerId != null && ids.Contains(book.OwnerId.Value))
            .GroupBy(book => book.OwnerId!.Value).Select(group => new { group.Key, Count = group.Count() }).ToDictionaryAsync(item => item.Key, item => item.Count, cancellationToken);
        return children.OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .Select(child => new ChildAccount(child.Id, child.Name, child.CreatedAt, counts.GetValueOrDefault(child.Id))).ToArray();
    }

    private static async Task<bool> IsMyChildAsync(ShelfDb db, int childId, CancellationToken cancellationToken) =>
        db.ReaderId != 0 && await db.Readers.AnyAsync(reader => reader.Id == childId && reader.GuardianId == db.ReaderId && reader.Restricted, cancellationToken);

    // A new password for a child, chosen by their guardian; it signs the child out everywhere.
    public static async Task<AccountProblem?> SetChildPasswordAsync(ShelfDb db, int childId, string? password, CancellationToken cancellationToken = default)
    {
        if (!await IsMyChildAsync(db, childId, cancellationToken))
        {
            return AccountProblem.NoSuchReader;
        }

        if (ReaderRules.PasswordProblem(password) is { } problem)
        {
            return problem;
        }

        await ReaderRules.SetPasswordAsync(db, childId, password!, cancellationToken);
        return null;
    }

    public static async Task<AccountProblem?> RemoveChildAsync(ShelfDb db, EbookStore ebooks, AudioStore audio, CoverStore covers, int childId, CancellationToken cancellationToken = default) =>
        await IsMyChildAsync(db, childId, cancellationToken) ? await ReaderRules.RemoveAsync(db, ebooks, audio, covers, childId, cancellationToken) : AccountProblem.NoSuchReader;

    // An admin can let a child's account be an ordinary one: once they are grown, or when their guardian has gone.
    public static async Task<bool> ReleaseAsync(ShelfDb db, int childId, CancellationToken cancellationToken = default)
    {
        var child = await db.Readers.FirstOrDefaultAsync(reader => reader.Id == childId && reader.Restricted, cancellationToken);
        if (child is null)
        {
            return false;
        }

        (child.Restricted, child.GuardianId) = (false, null);
        await db.SaveChangesAsync(cancellationToken);
        await Audit.NoteAsync(db, Say("Made a child's account an ordinary one"), child, cancellationToken: cancellationToken);
        return true;
    }

    public static async Task SetSharedAsync(ShelfDb db, int collectionId, bool shared, CancellationToken cancellationToken = default) =>
        await db.Collections.Where(collection => collection.Id == collectionId).ExecuteUpdateAsync(set => set.SetProperty(collection => collection.ForChildren, shared), cancellationToken);

    // Whether a book is one this child may read: in a collection their guardian shares with their children.
    public static async Task<Book?> SharedBookAsync(ShelfDb db, int bookId, CancellationToken cancellationToken = default)
    {
        if (await GuardianOfAsync(db, db.ReaderId, cancellationToken) is not int guardian)
        {
            return null;
        }

        var shared = await db.CollectionBooks.IgnoreQueryFilters().AnyAsync(entry => entry.BookId == bookId && entry.Collection!.OwnerId == guardian && entry.Collection.ForChildren, cancellationToken);
        return shared ? await db.Books.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(book => book.Id == bookId && book.OwnerId == guardian, cancellationToken) : null;
    }

    public static async Task<(string Guardian, FamilyBook[] Books)?> SharedBooksAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        if (await GuardianOfAsync(db, db.ReaderId, cancellationToken) is not int guardian)
        {
            return null;
        }

        var name = await db.Readers.AsNoTracking().Where(reader => reader.Id == guardian).Select(reader => reader.Name).FirstAsync(cancellationToken);
        var rows = await db.CollectionBooks.IgnoreQueryFilters().AsNoTracking()
            .Where(entry => entry.Collection!.OwnerId == guardian && entry.Collection.ForChildren && entry.Book!.OwnerId == guardian)
            .Select(entry => new { entry.Book, Collection = entry.Collection!.Name })
            .ToListAsync(cancellationToken);
        var books = rows
            .GroupBy(row => row.Book!.Id)
            .Select(group => group.First())
            .OrderBy(row => row.Book!.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(row => new FamilyBook(row.Book!.Id, row.Book.Title, row.Book.Author, row.Book.CoverShown, row.Book.EbookStoredName is not null, row.Book.AudioStoredName is not null, row.Collection))
            .ToArray();
        return (name, books);
    }
}

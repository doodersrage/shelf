using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

public static class Covers
{
    public static string? For(int id, string? coverUrl, bool fileCover) =>
        !string.IsNullOrWhiteSpace(coverUrl) ? coverUrl : fileCover ? $"/books/{id}/cover" : null;

    // What Shelf keeps about the e-book now on a book: whether it has a cover picture, and what KOReader calls it.
    // Called whenever a file is attached or taken off.
    public static void Note(Book book, EbookStore store)
    {
        var path = store.OpenPath(book.EbookStoredName);
        book.FileCover = path is not null && EbookStore.IsEpub(book.EbookStoredName) && EpubFile.Cover(path) is not null;
        book.KoreaderDigest = Kosync.Digest(path);
    }

    // The picture itself, for the owner, a borrower, or anyone shown the book on an open shelf.
    public static async Task<IResult> File(int id, ShelfDb db, EbookStore store, HttpContext http, CancellationToken cancellationToken)
    {
        var me = db.ReaderId;
        var storedName = await db.Books.IgnoreQueryFilters().AsNoTracking()
            .Where(book => book.Id == id && book.FileCover && me != 0
                && (book.OwnerId == me || book.BorrowerId == me || (book.Owner != null && book.Owner.ShelfOpen)))
            .Select(book => book.EbookStoredName)
            .FirstOrDefaultAsync(cancellationToken);
        var path = store.OpenPath(storedName);
        if (path is null || EpubFile.Cover(path) is not { } cover)
        {
            return TypedResults.NotFound();
        }

        http.Response.Headers.CacheControl = "private, max-age=86400";
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'";
        return Results.File(cover.Bytes, cover.ContentType);
    }

    // Books from before covers and KOReader names were read from files: checked once, in the background.
    public static async Task<int> BackfillAsync(ShelfDb db, EbookStore store, CancellationToken cancellationToken)
    {
        var books = await db.Books.IgnoreQueryFilters()
            .Where(book => book.EbookStoredName != null && book.KoreaderDigest == null)
            .ToListAsync(cancellationToken);
        var found = 0;
        foreach (var book in books)
        {
            Note(book, store);
            found += book.FileCover ? 1 : 0;
        }

        await db.SaveChangesAsync(cancellationToken);
        return found;
    }
}

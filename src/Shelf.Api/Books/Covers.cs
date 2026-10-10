using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

public static class Covers
{
    // A picture kept on the shelf comes first, then the cover address given, then the e-book's own. The kept
    // picture's name goes in the address, so a new one is never mistaken for the old in a browser's cache.
    public static string? For(int id, string? coverUrl, bool fileCover, string? coverImage) =>
        coverImage is { Length: >= 8 } ? $"/books/{id}/cover?v={coverImage[..8]}"
        : !string.IsNullOrWhiteSpace(coverUrl) ? coverUrl
        : fileCover ? $"/books/{id}/cover"
        : null;

    // Whether a book shows any cover at all, so art found later does not replace one already chosen.
    public static bool HasCover(Book book) => book.CoverImage is not null || !string.IsNullOrWhiteSpace(book.CoverUrl) || book.FileCover;

    // What Shelf keeps about the e-book now on a book: whether it has a cover picture, and what KOReader calls it.
    // Called whenever a file is attached or taken off.
    public static void Note(Book book, EbookStore store)
    {
        var path = store.OpenPath(book.EbookStoredName);
        book.FileCover = path is not null && Picture(book.EbookStoredName, path) is not null;
        book.KoreaderDigest = Kosync.Digest(path);
    }

    // The picture itself, for the owner, a borrower, or anyone shown the book on an open shelf.
    public static async Task<IResult> File(int id, ShelfDb db, EbookStore store, CoverStore covers, HttpContext http, CancellationToken cancellationToken)
    {
        var me = db.ReaderId;
        var files = await db.Books.IgnoreQueryFilters().AsNoTracking()
            .Where(book => book.Id == id && (book.FileCover || book.CoverImage != null) && me != 0
                && (book.OwnerId == me || book.BorrowerId == me || (book.Owner != null && book.Owner.ShelfOpen)))
            .Select(book => new { book.EbookStoredName, book.CoverImage })
            .FirstOrDefaultAsync(cancellationToken);
        return Serve(files?.EbookStoredName, files?.CoverImage, store, covers, http);
    }

    // A book's kept picture, or else its e-book's own cover.
    public static IResult Serve(string? ebookStoredName, string? coverImage, EbookStore store, CoverStore covers, HttpContext http)
    {
        http.Response.Headers.CacheControl = "private, max-age=86400";
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'";
        if (covers.OpenPath(coverImage) is { } kept)
        {
            return Results.File(kept, CoverStore.ContentType(coverImage!));
        }

        var path = store.OpenPath(ebookStoredName);
        if (path is null || Picture(ebookStoredName, path) is not { } cover)
        {
            return TypedResults.NotFound();
        }

        return Results.File(cover.Bytes, cover.ContentType);
    }

    // A reader's own picture for a book, replacing any picture kept before.
    public static async Task<IResult> Upload(int id, IFormFile? file, ShelfDb db, CoverStore covers, CancellationToken cancellationToken)
    {
        var book = await db.Books.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (book is null)
        {
            return TypedResults.NotFound();
        }

        string? saved = null;
        if (file is { Length: > 0 and <= CoverStore.MaxBytes })
        {
            await using var stream = file.OpenReadStream();
            saved = await covers.SaveAsync(stream, cancellationToken);
        }

        if (saved is null)
        {
            return TypedResults.Redirect($"/library/{id}?cover=unsupported");
        }

        covers.Delete(book.CoverImage);
        book.CoverImage = saved;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Redirect($"/library/{id}?cover=saved");
    }

    public static async Task<Results<NoContent, NotFound>> Remove(int id, ShelfDb db, CoverStore covers, CancellationToken cancellationToken)
    {
        var book = await db.Books.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (book is null)
        {
            return TypedResults.NotFound();
        }

        covers.Delete(book.CoverImage);
        book.CoverImage = null;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    // Art inside an audiobook's first track, kept as the book's cover when it has none yet.
    public static async Task NoteAudioArtAsync(Book book, AudioStore audio, CoverStore covers, CancellationToken cancellationToken)
    {
        if (HasCover(book) || audio.TrackPath(book.AudioStoredName, 0) is not { } first || AudioDetails.EmbeddedCover(first) is not { } art)
        {
            return;
        }

        book.CoverImage = await covers.SaveAsync(art.Bytes, cancellationToken);
    }

    // An EPUB's cover picture, or a comic's first page.
    private static (byte[] Bytes, string ContentType)? Picture(string? storedName, string path) =>
        EbookStore.IsEpub(storedName) ? EpubFile.Cover(path)
        : EbookStore.IsComic(storedName) ? ComicFile.Page(path, 0)
        : null;

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

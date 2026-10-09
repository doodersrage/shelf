using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

public static class EbookEndpoints
{
    private const string ChapterPolicy = "sandbox; default-src 'none'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; font-src 'self' data:";

    public static async Task<IResult> Upload(
        int id,
        IFormFile? file,
        ShelfDb db,
        EbookStore store,
        CancellationToken cancellationToken)
    {
        var book = await db.Books.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (book is null)
        {
            return TypedResults.NotFound();
        }

        if (file is null || file.Length == 0)
        {
            return TypedResults.Redirect($"/library/{id}?ebook=unsupported");
        }

        await using var stream = file.OpenReadStream();
        var saved = await store.SaveAsync(stream, file.FileName, cancellationToken);
        if (saved.Status != EbookSaveStatus.Saved || saved.StoredName is null || saved.FileName is null)
        {
            var reason = saved.Status == EbookSaveStatus.TooLarge ? "large" : "unsupported";
            return TypedResults.Redirect($"/library/{id}?ebook={reason}");
        }

        store.Delete(book.EbookStoredName);
        book.EbookStoredName = saved.StoredName;
        book.EbookFileName = saved.FileName;
        book.EbookChapter = 0;
        book.Format ??= BookFormat.Ebook;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Redirect($"/library/{id}?ebook=saved");
    }

    public static async Task<Results<NoContent, NotFound>> Remove(
        int id,
        ShelfDb db,
        EbookStore store,
        CancellationToken cancellationToken)
    {
        var book = await db.Books.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (book is null)
        {
            return TypedResults.NotFound();
        }

        store.Delete(book.EbookStoredName);
        book.EbookStoredName = null;
        book.EbookFileName = null;
        book.EbookChapter = null;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    public static async Task<IResult> File(
        int id,
        ShelfDb db,
        EbookStore store,
        CancellationToken cancellationToken)
    {
        var book = await db.Books.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        var path = book is null ? null : store.OpenPath(book.EbookStoredName);
        if (book is null || path is null)
        {
            return TypedResults.NotFound();
        }

        var type = EbookStore.IsPdf(book.EbookStoredName) ? "application/pdf" : "application/epub+zip";
        return Results.File(path, type, enableRangeProcessing: true);
    }

    public static async Task<IResult> Chapter(
        int id,
        int index,
        HttpContext http,
        ShelfDb db,
        EbookStore store,
        CancellationToken cancellationToken)
    {
        var book = await db.Books.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        var path = book is null ? null : store.OpenPath(book.EbookStoredName);
        if (path is null || !EbookStore.IsEpub(book?.EbookStoredName))
        {
            return TypedResults.NotFound();
        }

        var html = EpubFile.ChapterHtml(path, index, $"/books/{id}/ebook/assets");
        if (html is null)
        {
            return TypedResults.NotFound();
        }

        http.Response.Headers.ContentSecurityPolicy = ChapterPolicy;
        return Results.Content(html, "text/html; charset=utf-8");
    }

    public static async Task<IResult> Asset(
        int id,
        string path,
        HttpContext http,
        ShelfDb db,
        EbookStore store,
        CancellationToken cancellationToken)
    {
        var book = await db.Books.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        var epub = book is null ? null : store.OpenPath(book.EbookStoredName);
        if (epub is null || !EbookStore.IsEpub(book?.EbookStoredName))
        {
            return TypedResults.NotFound();
        }

        var asset = EpubFile.Asset(epub, path);
        if (asset is null)
        {
            return TypedResults.NotFound();
        }

        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'none'";
        return Results.File(asset.Value.Bytes, asset.Value.ContentType);
    }
}

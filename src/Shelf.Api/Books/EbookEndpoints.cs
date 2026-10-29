using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

public static class EbookEndpoints
{
    private const string ChapterPolicy = "sandbox allow-scripts; default-src 'none'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; font-src 'self' data:; script-src 'unsafe-inline'";

    public static async Task<IResult> Upload(
        int id,
        IFormFile? file,
        [FromForm] bool? keepBoth,
        ShelfDb db,
        EbookStore store,
        OcrService ocr,
        IDataProtectionProvider protection,
        Storage storage,
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
            var reason = saved.Status switch
            {
                EbookSaveStatus.TooLarge => "large",
                EbookSaveStatus.NeedsConverter => "convert",
                EbookSaveStatus.Damaged => "damaged",
                _ => "unsupported",
            };
            return TypedResults.Redirect($"/library/{id}?ebook={reason}");
        }

        // Past the reader's storage limit, the file goes again.
        if (!await storage.FitsAsync(db, db.ReaderId, storage.EbookSize(saved.StoredName) - storage.EbookSize(book.EbookStoredName), cancellationToken))
        {
            store.Delete(saved.StoredName);
            return TypedResults.Redirect($"/library/{id}?ebook=full");
        }

        // The same bytes on another of this reader's books: hold the upload and ask before keeping a second copy.
        if (keepBoth != true
            && await Duplicates.FindEbookAsync(db, store, id, saved.StoredName, cancellationToken) is { } same
            && store.Hold(saved.StoredName))
        {
            var token = Duplicates.Hold(protection, new HeldUpload(db.ReaderId, id, UploadKind.Ebook, saved.StoredName, saved.FileName, same.Id));
            return TypedResults.Redirect($"/library/{id}?ebook=duplicate&held={Uri.EscapeDataString(token)}");
        }

        await AttachAsync(db, store, book, saved.StoredName, saved.FileName, cancellationToken);
        ocr.Nudge();

        return TypedResults.Redirect($"/library/{id}?ebook=saved");
    }

    // The old file goes only once the book names the new one: deleted first, a failed save left a book naming a file
    // that was gone, and its only copy lost.
    public static async Task AttachAsync(ShelfDb db, EbookStore store, Book book, string storedName, string fileName, CancellationToken cancellationToken)
    {
        var old = book.EbookStoredName;
        book.EbookStoredName = storedName;
        book.EbookFileName = fileName;
        book.EbookChapter = 0;
        Covers.Note(book, store);
        book.Format ??= BookFormat.Ebook;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            store.Delete(storedName);
            throw;
        }

        if (old != storedName)
        {
            store.Delete(old);
        }
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

        var old = book.EbookStoredName;
        book.EbookStoredName = null;
        book.EbookFileName = null;
        book.EbookChapter = null;
        book.FileCover = false;
        book.KoreaderDigest = null;
        await db.SaveChangesAsync(cancellationToken);
        store.Delete(old);
        return TypedResults.NoContent();
    }

    public static async Task<IResult> File(
        int id,
        ShelfDb db,
        EbookStore store,
        CancellationToken cancellationToken)
    {
        var open = await Lending.OpenAsync(db, id, cancellationToken);
        var book = open?.Book;
        var path = book is null ? null : store.OpenPath(book.EbookStoredName);
        if (book is null || path is null)
        {
            return TypedResults.NotFound();
        }

        // A whole EPUB is a download: a household member who may not download reads it a chapter at a time instead.
        // A PDF and a comic have no smaller way to be read here, and go whole.
        if (EbookStore.IsEpub(book.EbookStoredName) && !await Households.MayDownloadAsync(db, open!, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var type = EbookStore.ContentType(book.EbookStoredName);
        return Results.File(path, type, enableRangeProcessing: true);
    }

    // The file to keep, named for the book: an EPUB with the shelf's details written into a copy of it.
    public static async Task<IResult> Download(
        int id,
        ShelfDb db,
        EpubDetailsStore withDetails,
        CancellationToken cancellationToken)
    {
        var open = await Lending.OpenAsync(db, id, cancellationToken);
        if (open is not { Book: var book } || !await Households.MayDownloadAsync(db, open, cancellationToken) || await withDetails.PathForAsync(book, cancellationToken) is not { } path)
        {
            return TypedResults.NotFound();
        }

        var name = NotesExport.FileName(book)[..^".md".Length] + Path.GetExtension(book.EbookStoredName)!.ToLowerInvariant();
        return Results.File(path, EbookStore.ContentType(book.EbookStoredName), name, enableRangeProcessing: true);
    }

    public static async Task<IResult> Chapter(
        int id,
        int index,
        HttpContext http,
        ShelfDb db,
        EbookStore store,
        CancellationToken cancellationToken)
    {
        var open = await Lending.OpenAsync(db, id, cancellationToken);
        var path = open is null ? null : store.OpenPath(open.Book.EbookStoredName);
        if (open is null || path is null || !EbookStore.IsEpub(open.Book.EbookStoredName))
        {
            return TypedResults.NotFound();
        }

        var html = EpubFile.ChapterHtml(path, index, $"/books/{id}/ebook/assets");
        if (html is null)
        {
            return TypedResults.NotFound();
        }

        var marks = await Lending.Marks(db, open).AsNoTracking()
            .Where(item => item.ChapterIndex == index)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);
        html = ReaderMarks.Inject(html, marks);
        html = ReaderMarks.Typeset(html, await BookRules.GetReaderTypeAsync(db, cancellationToken));
        http.Response.Headers.ContentSecurityPolicy = ChapterPolicy;
        return Results.Content(html, "text/html; charset=utf-8");
    }

    // One page of a comic, as the image it is.
    public static async Task<IResult> ComicPage(int id, int index, HttpContext http, ShelfDb db, EbookStore store, CancellationToken cancellationToken)
    {
        var book = (await Lending.OpenAsync(db, id, cancellationToken))?.Book;
        var path = book is null || !EbookStore.IsComic(book.EbookStoredName) ? null : store.OpenPath(book.EbookStoredName);
        if (path is null || ComicFile.Page(path, index) is not { } page)
        {
            return TypedResults.NotFound();
        }

        http.Response.Headers.CacheControl = "private, max-age=86400";
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'";
        return Results.File(page.Bytes, page.ContentType);
    }

    public static async Task<IResult> Asset(
        int id,
        string path,
        HttpContext http,
        ShelfDb db,
        EbookStore store,
        CancellationToken cancellationToken)
    {
        var book = (await Lending.OpenAsync(db, id, cancellationToken))?.Book;
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

    public static async Task<Results<Ok<HighlightResponse[]>, NotFound>> ListHighlights(
        int id,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        if (await Lending.OpenAsync(db, id, cancellationToken) is not { } open)
        {
            return TypedResults.NotFound();
        }

        var marks = await Lending.Marks(db, open).AsNoTracking()
            .OrderBy(item => item.ChapterIndex)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(marks.Select(HighlightResponse.From).ToArray());
    }

    public static async Task<Results<Created<HighlightResponse>, NotFound, ValidationProblem>> CreateHighlight(
        int id,
        CreateHighlightRequest request,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        if (await Lending.OpenAsync(db, id, cancellationToken) is not { } open)
        {
            return TypedResults.NotFound();
        }

        var problems = BookRules.ValidateHighlight(request);
        if (problems is not null)
        {
            return TypedResults.ValidationProblem(problems);
        }

        var highlight = Lending.NewMark(
            db,
            open,
            request.ChapterIndex,
            request.Text.Trim(),
            string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            Clip(request.Prefix, keepEnd: true),
            Clip(request.Suffix, keepEnd: false));
        db.Highlights.Add(highlight);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/books/{id}/highlights/{highlight.Id}", HighlightResponse.From(highlight));
    }

    public static async Task<Results<Ok<HighlightResponse>, NotFound, ValidationProblem>> UpdateHighlight(
        int id,
        int highlightId,
        UpdateHighlightRequest request,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        var open = await Lending.OpenAsync(db, id, cancellationToken);
        var highlight = open is null
            ? null
            : await Lending.Marks(db, open).FirstOrDefaultAsync(item => item.Id == highlightId, cancellationToken);
        if (highlight is null)
        {
            return TypedResults.NotFound();
        }

        if (!string.IsNullOrWhiteSpace(request.Note) && request.Note.Trim().Length > 2000)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.Note)] = [Localization.Words.T("Keep the note to 2000 characters.")],
            });
        }

        highlight.Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(HighlightResponse.From(highlight));
    }

    public static async Task<Results<NoContent, NotFound>> DeleteHighlight(
        int id,
        int highlightId,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        var open = await Lending.OpenAsync(db, id, cancellationToken);
        var highlight = open is null
            ? null
            : await Lending.Marks(db, open).FirstOrDefaultAsync(item => item.Id == highlightId, cancellationToken);
        if (highlight is null)
        {
            return TypedResults.NotFound();
        }

        // The owner's go to the trash; a borrower's own are theirs to delete outright.
        if (open!.Borrowed)
        {
            db.Highlights.Remove(highlight);
            await db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            await Trash.DeleteRowAsync(db, highlight, TrashKind.Highlight, id, highlight.Text, open.Book.Title, cancellationToken);
        }

        return TypedResults.NoContent();
    }

    private static string? Clip(string? value, bool keepEnd)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (value.Length <= 80)
        {
            return value;
        }

        return keepEnd ? value[^80..] : value[..80];
    }
}

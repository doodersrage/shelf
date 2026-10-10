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

    public static async Task AttachAsync(ShelfDb db, EbookStore store, Book book, string storedName, string fileName, CancellationToken cancellationToken)
    {
        store.Delete(book.EbookStoredName);
        book.EbookStoredName = storedName;
        book.EbookFileName = fileName;
        book.EbookChapter = 0;
        Covers.Note(book, store);
        book.Format ??= BookFormat.Ebook;
        await db.SaveChangesAsync(cancellationToken);
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
        book.FileCover = false;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    public static async Task<IResult> File(
        int id,
        ShelfDb db,
        EbookStore store,
        CancellationToken cancellationToken)
    {
        var book = (await Lending.OpenAsync(db, id, cancellationToken))?.Book;
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
                [nameof(request.Note)] = ["Keep the note to 2000 characters."],
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

        db.Highlights.Remove(highlight);
        await db.SaveChangesAsync(cancellationToken);
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

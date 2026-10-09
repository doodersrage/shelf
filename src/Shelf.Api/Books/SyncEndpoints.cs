using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

public static class SyncEndpoints
{
    public const string Policy = "sync";

    public static async Task<Ok<SyncCatalog>> Catalog(
        ShelfDb db,
        EbookStore ebooks,
        AudioStore audio,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await ShelfSync.CatalogAsync(db, ebooks, audio, cancellationToken));

    public static async Task<Results<Ok<SyncPlace>, ValidationProblem>> Ensure(
        SyncOffer offer,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(offer.Title) || string.IsNullOrWhiteSpace(offer.Author)
            || offer.Title.Trim().Length > 200 || offer.Author.Trim().Length > 200)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(offer.Title)] = ["A title and an author are required."],
            });
        }

        var books = await db.Books.ToListAsync(cancellationToken);
        var book = books.FirstOrDefault(item => ShelfSync.SameBook(item.Isbn, item.Title, item.Author, offer.Isbn, offer.Title, offer.Author));
        if (book is null)
        {
            book = new Book
            {
                Title = offer.Title.Trim(),
                Author = offer.Author.Trim(),
                Isbn = BookRules.NormalizeIsbn(offer.Isbn),
                AddedAt = DateTimeOffset.UtcNow,
            };
            db.Books.Add(book);
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (book.Isbn is null && BookRules.NormalizeIsbn(offer.Isbn) is string isbn)
        {
            book.Isbn = isbn;
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(new SyncPlace(ShelfSync.Key(book.Isbn, book.Title, book.Author)));
    }

    public static async Task<IResult> Ebook(
        string key,
        ShelfDb db,
        EbookStore store,
        CancellationToken cancellationToken)
    {
        var book = await FindAsync(db, key, cancellationToken);
        var path = book is null ? null : store.OpenPath(book.EbookStoredName);
        if (path is null)
        {
            return TypedResults.NotFound();
        }

        var type = EbookStore.IsPdf(book!.EbookStoredName) ? "application/pdf" : "application/epub+zip";
        return Results.File(path, type, enableRangeProcessing: true);
    }

    public static async Task<IResult> Audio(
        string key,
        ShelfDb db,
        AudioStore store,
        CancellationToken cancellationToken)
    {
        var book = await FindAsync(db, key, cancellationToken);
        if (book is null || store.Tracks(book.AudioStoredName).Count == 0)
        {
            return TypedResults.NotFound();
        }

        var temp = Path.Combine(Path.GetTempPath(), $"shelf-sync-{Guid.NewGuid():N}.zip");
        await using (var output = File.Create(temp))
        {
            await store.WriteZipAsync(book.AudioStoredName, output, cancellationToken);
        }

        return new TempZipResult(temp);
    }

    private sealed class TempZipResult(string path) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            try
            {
                httpContext.Response.ContentType = "application/zip";
                await using var input = File.OpenRead(path);
                await input.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    public static async Task<Results<NoContent, NotFound, Conflict, BadRequest>> UploadEbook(
        string key,
        IFormFile? file,
        ShelfDb db,
        EbookStore store,
        CancellationToken cancellationToken)
    {
        var book = await FindAsync(db, key, cancellationToken);
        if (book is null)
        {
            return TypedResults.NotFound();
        }

        if (file is null || file.Length == 0)
        {
            return TypedResults.BadRequest();
        }

        await using var stream = file.OpenReadStream();
        var saved = await store.SaveAsync(stream, file.FileName, cancellationToken);
        if (saved.Status != EbookSaveStatus.Saved || saved.StoredName is null || saved.FileName is null)
        {
            return TypedResults.BadRequest();
        }

        var incoming = await store.HashAsync(saved.StoredName, cancellationToken);
        if (incoming is null)
        {
            store.Delete(saved.StoredName);
            return TypedResults.BadRequest();
        }

        var current = await store.HashAsync(book.EbookStoredName, cancellationToken);
        if (current is not null && current != incoming)
        {
            store.Delete(saved.StoredName);
            return TypedResults.Conflict();
        }

        if (current == incoming)
        {
            store.Delete(saved.StoredName);
            return TypedResults.NoContent();
        }

        store.Delete(book.EbookStoredName);
        book.EbookStoredName = saved.StoredName;
        book.EbookFileName = saved.FileName;
        book.EbookChapter ??= 0;
        book.Format ??= BookFormat.Ebook;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    public static async Task<Results<NoContent, NotFound, Conflict, BadRequest>> UploadAudio(
        string key,
        IFormFile? file,
        ShelfDb db,
        AudioStore store,
        CancellationToken cancellationToken)
    {
        var book = await FindAsync(db, key, cancellationToken);
        if (book is null)
        {
            return TypedResults.NotFound();
        }

        if (file is null || file.Length == 0)
        {
            return TypedResults.BadRequest();
        }

        await using var stream = file.OpenReadStream();
        var saved = await store.SaveAsync([new AudioUpload(file.FileName, stream, file.Length)], cancellationToken);
        if (saved.Status != AudioSaveStatus.Saved || saved.StoredName is null || saved.FileName is null)
        {
            return TypedResults.BadRequest();
        }

        var incoming = await store.HashAsync(saved.StoredName, cancellationToken);
        if (incoming is null)
        {
            store.Delete(saved.StoredName);
            return TypedResults.BadRequest();
        }

        var current = await store.HashAsync(book.AudioStoredName, cancellationToken);
        if (current is not null && current != incoming)
        {
            store.Delete(saved.StoredName);
            return TypedResults.Conflict();
        }

        if (current == incoming)
        {
            store.Delete(saved.StoredName);
            return TypedResults.NoContent();
        }

        store.Delete(book.AudioStoredName);
        book.AudioStoredName = saved.StoredName;
        book.AudioFileName = saved.FileName;
        book.AudioTrack ??= 0;
        book.AudioSeconds ??= 0;
        book.Format ??= BookFormat.Audiobook;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    public static async Task<Results<NoContent, NotFound>> Progress(
        string key,
        SyncProgress progress,
        ShelfDb db,
        EbookStore ebooks,
        AudioStore audio,
        CancellationToken cancellationToken)
    {
        var book = await db.Books.Include(item => item.Highlights).ToListAsync(cancellationToken);
        var match = book.FirstOrDefault(item => ShelfSync.Key(item.Isbn, item.Title, item.Author) == key);
        if (match is null)
        {
            return TypedResults.NotFound();
        }

        var ebookHash = await ebooks.HashAsync(match.EbookStoredName, cancellationToken);
        var audioHash = await audio.HashAsync(match.AudioStoredName, cancellationToken);
        if (ShelfSync.ApplyProgress(match, ebookHash, audioHash, progress))
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.NoContent();
    }

    private static async Task<Book?> FindAsync(ShelfDb db, string key, CancellationToken cancellationToken)
    {
        var books = await db.Books.ToListAsync(cancellationToken);
        return books.FirstOrDefault(book => ShelfSync.Key(book.Isbn, book.Title, book.Author) == key);
    }
}

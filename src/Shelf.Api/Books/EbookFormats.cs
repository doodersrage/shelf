using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

// Another format of a book's e-book, kept beside the main one as Calibre keeps them: an AZW3 for a Kindle, a PDF
// with the printed pages. The reader, the place, and sync go by the main e-book; these are to download and to send
// on, and any of them Shelf's reader opens can be made the main one. One of each format to a book.
public sealed class EbookFormat
{
    public const int MaxFileNameLength = 200;

    public int Id { get; set; }
    public int BookId { get; set; }
    public Book? Book { get; set; }
    public required string StoredName { get; set; }
    public required string FileName { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

public sealed record EbookFormatResponse(int Id, string Format, string FileName, long Size, bool CanBeMain);

public static class EbookFormats
{
    public static void Map(RouteGroupBuilder books)
    {
        books.MapGet("/{id:int}/ebook/formats", async Task<IResult> (int id, ShelfDb db, EbookStore store, CancellationToken cancellationToken) =>
            await ListAsync(db, store, id, cancellationToken) is { } formats ? TypedResults.Ok(formats) : TypedResults.NotFound()).WithTags("Files");
        books.MapPost("/{id:int}/ebook/formats", Upload).DisableAntiforgery().WithTags("Files");
        books.MapGet("/{id:int}/ebook/formats/{formatId:int}", Download).WithTags("Files");
        books.MapDelete("/{id:int}/ebook/formats/{formatId:int}", async Task<IResult> (int id, int formatId, ShelfDb db, EbookStore store, CancellationToken cancellationToken) =>
            await RemoveAsync(db, store, id, formatId, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound()).WithTags("Files");
        books.MapPost("/{id:int}/ebook/formats/{formatId:int}/main", async Task<IResult> (int id, int formatId, ShelfDb db, EbookStore store, CancellationToken cancellationToken) =>
            await MakeMainAsync(db, store, id, formatId, cancellationToken) switch
            {
                true => TypedResults.NoContent(),
                false => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Format"] = [Localization.Words.T("Shelf's reader opens an EPUB, a PDF, or a comic; this format can't be the main e-book.")] }),
                null => TypedResults.NotFound(),
            }).WithTags("Files");
    }

    // A book's other formats, for its owner or a reader it's lent to; null when the book isn't open to them.
    public static async Task<EbookFormatResponse[]?> ListAsync(ShelfDb db, EbookStore store, int bookId, CancellationToken cancellationToken = default)
    {
        if (await Lending.OpenAsync(db, bookId, cancellationToken) is null)
        {
            return null;
        }

        var formats = await db.EbookFormats.IgnoreQueryFilters().AsNoTracking().Where(format => format.BookId == bookId).ToListAsync(cancellationToken);
        return [.. formats.OrderBy(format => EbookStore.FormatOf(format.StoredName), StringComparer.Ordinal).Select(format => new EbookFormatResponse(
            format.Id, EbookStore.FormatOf(format.StoredName), format.FileName,
            store.OpenPath(format.StoredName) is { } path ? new FileInfo(path).Length : 0, EbookStore.IsReadable(format.StoredName)))];
    }

    // Another format kept for a book: the one before of the same format goes once this one is kept.
    public static async Task<string> AddAsync(ShelfDb db, EbookStore store, Storage storage, Book book, Stream source, string fileName, CancellationToken cancellationToken = default)
    {
        var saved = await store.SaveAsync(source, fileName, cancellationToken, keepAsIs: true);
        if (saved.Status != EbookSaveStatus.Saved || saved.StoredName is null || saved.FileName is null)
        {
            return saved.Status switch
            {
                EbookSaveStatus.TooLarge => "large",
                EbookSaveStatus.Damaged => "damaged",
                _ => "unsupported",
            };
        }

        var format = EbookStore.FormatOf(saved.StoredName);
        if (EbookStore.FormatOf(book.EbookStoredName) == format)
        {
            store.Delete(saved.StoredName);
            return "main";
        }

        var existing = (await db.EbookFormats.Where(item => item.BookId == book.Id).ToListAsync(cancellationToken))
            .FirstOrDefault(item => EbookStore.FormatOf(item.StoredName) == format);
        if (!await storage.FitsAsync(db, db.ReaderId, storage.EbookSize(saved.StoredName) - storage.EbookSize(existing?.StoredName), cancellationToken))
        {
            store.Delete(saved.StoredName);
            return "full";
        }

        var old = existing?.StoredName;
        if (existing is null)
        {
            db.EbookFormats.Add(new EbookFormat { BookId = book.Id, StoredName = saved.StoredName, FileName = saved.FileName, AddedAt = DateTimeOffset.UtcNow });
        }
        else
        {
            (existing.StoredName, existing.FileName, existing.AddedAt) = (saved.StoredName, saved.FileName, DateTimeOffset.UtcNow);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            store.Delete(saved.StoredName);
            throw;
        }

        store.Delete(old);
        return "saved";
    }

    public static async Task<bool> RemoveAsync(ShelfDb db, EbookStore store, int bookId, int formatId, CancellationToken cancellationToken = default)
    {
        if (await db.EbookFormats.FirstOrDefaultAsync(format => format.Id == formatId && format.BookId == bookId, cancellationToken) is not { } format)
        {
            return false;
        }

        db.EbookFormats.Remove(format);
        await db.SaveChangesAsync(cancellationToken);
        store.Delete(format.StoredName);
        return true;
    }

    // One the reader opens becomes the main e-book; the main one before stays as another format. The place starts at
    // the beginning, as a place in one file means nothing in another. Null when there's no such format; false when
    // the reader can't open it.
    public static async Task<bool?> MakeMainAsync(ShelfDb db, EbookStore store, int bookId, int formatId, CancellationToken cancellationToken = default)
    {
        var book = await db.Books.FirstOrDefaultAsync(item => item.Id == bookId, cancellationToken);
        if (book is null || await db.EbookFormats.FirstOrDefaultAsync(format => format.Id == formatId && format.BookId == bookId, cancellationToken) is not { } format)
        {
            return null;
        }

        if (!EbookStore.IsReadable(format.StoredName))
        {
            return false;
        }

        var (oldStored, oldName) = (book.EbookStoredName, book.EbookFileName);
        (book.EbookStoredName, book.EbookFileName) = (format.StoredName, format.FileName);
        (book.EbookChapter, book.EbookCfi, book.EbookProgress) = (0, null, null);
        Covers.Note(book, store);
        if (oldStored is not null)
        {
            (format.StoredName, format.FileName, format.AddedAt) = (oldStored, oldName ?? oldStored, DateTimeOffset.UtcNow);
        }
        else
        {
            db.EbookFormats.Remove(format);
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static async Task<IResult> Upload(int id, IFormFile? file, ShelfDb db, EbookStore store, Storage storage, CancellationToken cancellationToken)
    {
        if (await db.Books.FirstOrDefaultAsync(item => item.Id == id, cancellationToken) is not { } book)
        {
            return TypedResults.NotFound();
        }

        if (file is null || file.Length == 0)
        {
            return TypedResults.Redirect($"/library/{id}?format=unsupported");
        }

        await using var stream = file.OpenReadStream();
        return TypedResults.Redirect($"/library/{id}?format={await AddAsync(db, store, storage, book, stream, file.FileName, cancellationToken)}");
    }

    // Named for the book, as the main e-book's download is: "Author - Title.azw3".
    private static async Task<IResult> Download(int id, int formatId, ShelfDb db, EbookStore store, CancellationToken cancellationToken)
    {
        if (await Lending.OpenAsync(db, id, cancellationToken) is not { } open
            || await db.EbookFormats.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(format => format.Id == formatId && format.BookId == id, cancellationToken) is not { } format
            || store.OpenPath(format.StoredName) is not { } path)
        {
            return TypedResults.NotFound();
        }

        var name = NotesExport.FileName(open.Book)[..^".md".Length] + Path.GetExtension(format.StoredName).ToLowerInvariant();
        return Results.File(path, EbookStore.ContentType(format.StoredName), name, enableRangeProcessing: true);
    }
}

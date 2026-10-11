using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// A book as a shared list shows it to anyone with the link: what a catalog says, and the reader's stars. Never notes,
// reviews, quotes, loans, places, or files.
public sealed record SharedBook(int Id, string Title, string Author, string? Subtitle, int? Year, string? Series, int? SeriesNumber, int? Rating, string? CoverUrl);

public sealed record SharedList(string Name, string Reader, SharedBook[] Books, string? Description = null);

// A saved search or a collection a reader shares through a link of its own. The link is a long random token: whoever has it sees
// the list as it stands, and stopping sharing (or forgetting the search) ends it. A new link replaces the old.
public static class SharedLists
{
    public const int MaxBooks = 500;

    public static void Map(WebApplication app)
    {
        app.MapGet("/shared/{token}/covers/{id:int}", Cover).AllowAnonymous().ExcludeFromDescription();
        app.MapGet("/shared/{token}/list.json", Json).AllowAnonymous().WithTags("Sharing");
    }

    public static async Task<string> ShareAsync(ShelfDb db, int savedId, CancellationToken cancellationToken = default)
    {
        var saved = await db.SavedSearches.FirstAsync(item => item.Id == savedId, cancellationToken);
        saved.ShareToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(18));
        await db.SaveChangesAsync(cancellationToken);
        await Audit.NoteAsync(db, Say("Shared a reading list by link"), detail: saved.Name, cancellationToken: cancellationToken);
        return saved.ShareToken;
    }

    public static async Task StopAsync(ShelfDb db, int savedId, CancellationToken cancellationToken = default)
    {
        var saved = await db.SavedSearches.FirstOrDefaultAsync(item => item.Id == savedId, cancellationToken);
        if (saved?.ShareToken is null)
        {
            return;
        }

        saved.ShareToken = null;
        await db.SaveChangesAsync(cancellationToken);
        await Audit.NoteAsync(db, Say("Stopped sharing a reading list"), detail: saved.Name, cancellationToken: cancellationToken);
    }

    // The list behind a link, as it stands now; null when the link is not (or no longer) one.
    public static async Task<SharedList?> FindAsync(ShelfDb db, string? token, CancellationToken cancellationToken = default)
    {
        var books = await BooksAsync(db, token, cancellationToken);
        return books is null ? null : new SharedList(books.Value.Name, books.Value.Reader, books.Value.Books.Select(book => Show(book, token!)).ToArray(), books.Value.Description);
    }

    private static async Task<(string Name, string? Description, string Reader, List<Book> Books)?> BooksAsync(ShelfDb db, string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 40)
        {
            return null;
        }

        var saved = await db.SavedSearches.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(item => item.ShareToken == token, cancellationToken);
        if (saved is null)
        {
            return await CollectionAsync(db, token, cancellationToken);
        }

        var reader = await db.Readers.AsNoTracking().Where(item => item.Id == saved.OwnerId).Select(item => item.Name).FirstOrDefaultAsync(cancellationToken);
        if (reader is null)
        {
            return null;
        }

        var query = QueryHelpers.ParseQuery(saved.Query);
        string? Text(string name) => query.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.ToString() : null;
        bool On(string name) => Text(name) == "1";

        var owned = db.Books.IgnoreQueryFilters().Where(book => book.OwnerId == saved.OwnerId);
        var books = await BookRules.Filtered(
                owned,
                Text("q"),
                Enum.TryParse<BookStatus>(Text("status"), ignoreCase: true, out var status) ? status : null,
                Text("tag"),
                Text("author"),
                On("loved") ? true : null,
                On("loaned") ? true : null,
                Enum.TryParse<BookFormat>(Text("format"), ignoreCase: true, out var format) ? format : null,
                Text("series"),
                Text("place"),
                Text("recommendedBy"),
                Text("loanedTo"))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        IEnumerable<Book> kept = books;
        if (On("queued"))
        {
            kept = kept.Where(book => book.Queued);
        }

        if (On("file"))
        {
            kept = kept.Where(book => book.EbookStoredName is not null);
        }

        if (On("audio"))
        {
            kept = kept.Where(book => book.AudioStoredName is not null);
        }

        return (saved.Name, null, reader, BookRules.Sort(kept.ToList(), Text("sort")).Take(MaxBooks).ToList());
    }

    // A shared collection: its books in the order its reader put them, and its description.
    private static async Task<(string Name, string? Description, string Reader, List<Book> Books)?> CollectionAsync(ShelfDb db, string token, CancellationToken cancellationToken)
    {
        var collection = await db.Collections.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(item => item.ShareToken == token, cancellationToken);
        var reader = collection is null ? null : await db.Readers.AsNoTracking().Where(item => item.Id == collection.OwnerId).Select(item => item.Name).FirstOrDefaultAsync(cancellationToken);
        if (collection is null || reader is null)
        {
            return null;
        }

        var order = await db.CollectionBooks.IgnoreQueryFilters().AsNoTracking()
            .Where(entry => entry.CollectionId == collection.Id)
            .OrderBy(entry => entry.Position)
            .Select(entry => entry.BookId)
            .ToListAsync(cancellationToken);
        var books = await db.Books.IgnoreQueryFilters().AsNoTracking()
            .Where(book => book.OwnerId == collection.OwnerId && order.Contains(book.Id))
            .ToListAsync(cancellationToken);
        return (collection.Name, collection.Description, reader, order.Select(id => books.FirstOrDefault(book => book.Id == id)).OfType<Book>().Take(MaxBooks).ToList());
    }

    private static SharedBook Show(Book book, string token) => new(
        book.Id,
        book.Title,
        book.Author,
        book.Subtitle,
        book.Year,
        book.Series,
        book.SeriesNumber,
        book.Rating,
        // A picture kept on the shelf comes through the link; a cover from elsewhere is linked as it is.
        book.CoverImage is not null || (string.IsNullOrWhiteSpace(book.CoverUrl) && book.FileCover) ? $"/shared/{token}/covers/{book.Id}" : book.CoverUrl);

    private static async Task<IResult> Json(string token, ShelfDb db, CancellationToken cancellationToken) =>
        await FindAsync(db, token, cancellationToken) is { } list
            ? TypedResults.Ok(list)
            : TypedResults.NotFound();

    // A cover, for a book on the list only.
    private static async Task<IResult> Cover(string token, int id, ShelfDb db, EbookStore store, CoverStore covers, HttpContext http, CancellationToken cancellationToken)
    {
        var books = await BooksAsync(db, token, cancellationToken);
        var book = books?.Books.FirstOrDefault(item => item.Id == id);
        return book is null ? TypedResults.NotFound() : Covers.Serve(book.EbookStoredName, book.CoverImage, store, covers, http);
    }
}

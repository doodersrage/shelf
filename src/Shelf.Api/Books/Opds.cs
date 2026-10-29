using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

// An OPDS 1.2 catalog, so e-reader apps such as KOReader can browse a reader's e-books and download them.
// It is signed in to with the reader's device key, like sync.
public static class Opds
{
    private const string Navigation = "application/atom+xml;profile=opds-catalog;kind=navigation";
    private const string Acquisition = "application/atom+xml;profile=opds-catalog;kind=acquisition";

    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Dc = "http://purl.org/dc/terms/";

    public static void MapOpds(this IEndpointRouteBuilder app)
    {
        var opds = app.MapGroup("/opds").WithTags("OPDS").RequireAuthorization(SyncEndpoints.Policy).ExcludeFromDescription();
        opds.MapGet("/", Root);
        opds.MapGet("/books", Books);
        opds.MapGet("/borrowed", Borrowed);
        opds.MapGet("/shared", Shared);
        opds.MapGet("/books/{id:int}/file", File);
        opds.MapGet("/books/{id:int}/formats/{formatId:int}", Format);
    }

    private static IResult Root(ShelfDb db)
    {
        var feed = Feed("urn:shelf:opds", "Shelf", "/opds", Navigation);
        foreach (var (id, title, href, text) in new[]
        {
            ("reading", Localization.Words.T("Reading"), "/opds/books?status=Reading", Localization.Words.T("The e-books you are reading.")),
            ("want", Localization.Words.T("Want to read"), "/opds/books?status=Want", Localization.Words.T("The e-books on your want list.")),
            ("finished", Localization.Words.T("Finished"), "/opds/books?status=Finished", Localization.Words.T("The e-books you have finished.")),
            ("all", Localization.Words.T("All e-books"), "/opds/books", Localization.Words.T("Every e-book on your shelf.")),
            ("borrowed", Localization.Words.T("Lent to you"), "/opds/borrowed", Localization.Words.T("E-books other readers have lent you.")),
            ("shared", Localization.Words.T("Shared with you"), "/opds/shared", Localization.Words.T("E-books in the household libraries and collections shared with you, where you may download them.")),
        })
        {
            feed.Add(new XElement(Atom + "entry",
                new XElement(Atom + "id", $"urn:shelf:opds:{id}"),
                new XElement(Atom + "title", title),
                new XElement(Atom + "updated", DateTimeOffset.UtcNow.ToString("o")),
                new XElement(Atom + "content", new XAttribute("type", "text"), text),
                Link("subsection", href, Acquisition)));
        }

        return Xml(feed, Navigation);
    }

    private static async Task<IResult> Books(ShelfDb db, CancellationToken cancellationToken, BookStatus? status = null)
    {
        var books = await db.Books.AsNoTracking()
            .Where(book => book.EbookStoredName != null && (status == null || book.Status == status))
            .ToListAsync(cancellationToken);
        var title = status is { } chosen ? BookRules.StatusLabel(chosen) : Localization.Words.T("All e-books");
        return Xml(Acquire($"urn:shelf:opds:books:{status}", title, status is null ? "/opds/books" : $"/opds/books?status={status}", books, await FormatsAsync(db, books, cancellationToken)), Acquisition);
    }

    private static async Task<IResult> Borrowed(ShelfDb db, CancellationToken cancellationToken)
    {
        var readerId = db.ReaderId;
        var books = await db.Books.IgnoreQueryFilters().AsNoTracking()
            .Where(book => readerId != 0 && book.BorrowerId == readerId && book.EbookStoredName != null)
            .ToListAsync(cancellationToken);
        return Xml(Acquire("urn:shelf:opds:borrowed", "Lent to you", "/opds/borrowed", books, await FormatsAsync(db, books, cancellationToken)), Acquisition);
    }

    // A file taken to a device is a download: refused to a household member who may not download.
    // Books shared with the reader that they may take to a device: a guardian's collection's, and a household
    // library's where their membership allows downloads.
    private static async Task<IResult> Shared(ShelfDb db, CancellationToken cancellationToken)
    {
        var ids = await Households.DownloadableIdsAsync(db, cancellationToken);
        if (await Readers.Family.SharedBooksAsync(db, cancellationToken) is { } family)
        {
            ids.UnionWith(family.Books.Select(book => book.Id));
        }

        var books = await db.Books.IgnoreQueryFilters().AsNoTracking()
            .Where(book => ids.Contains(book.Id) && book.EbookStoredName != null)
            .ToListAsync(cancellationToken);
        return Xml(Acquire("urn:shelf:opds:shared", Localization.Words.T("Shared with you"), "/opds/shared", books, await FormatsAsync(db, books, cancellationToken)), Acquisition);
    }

    private static async Task<IResult> File(int id, ShelfDb db, EbookStore store, CancellationToken cancellationToken)
    {
        var open = await Lending.OpenAsync(db, id, cancellationToken);
        var book = open is not null && await Households.MayDownloadAsync(db, open, cancellationToken) ? open.Book : null;
        var path = book is null ? null : store.OpenPath(book.EbookStoredName);
        if (book is null || path is null)
        {
            return TypedResults.NotFound();
        }

        return Results.File(path, MediaType(book.EbookStoredName), book.EbookFileName ?? Path.GetFileName(path), enableRangeProcessing: true);
    }

    // A book's other formats, each a download beside the main e-book, as the app on the device can choose.
    private static async Task<ILookup<int, EbookFormat>> FormatsAsync(ShelfDb db, List<Book> books, CancellationToken cancellationToken)
    {
        var ids = books.Select(book => book.Id).ToList();
        return (await db.EbookFormats.IgnoreQueryFilters().AsNoTracking().Where(format => ids.Contains(format.BookId)).ToListAsync(cancellationToken)).ToLookup(format => format.BookId);
    }

    private static async Task<IResult> Format(int id, int formatId, ShelfDb db, EbookStore store, CancellationToken cancellationToken)
    {
        if (await Lending.OpenAsync(db, id, cancellationToken) is not { } open
            || !await Households.MayDownloadAsync(db, open, cancellationToken)
            || await db.EbookFormats.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(format => format.Id == formatId && format.BookId == id, cancellationToken) is not { } format
            || store.OpenPath(format.StoredName) is not { } path)
        {
            return TypedResults.NotFound();
        }

        return Results.File(path, EbookStore.ContentType(format.StoredName), format.FileName, enableRangeProcessing: true);
    }

    private static XElement Acquire(string id, string title, string self, IEnumerable<Book> books, ILookup<int, EbookFormat>? formats = null)
    {
        var feed = Feed(id, title, self, Acquisition);
        foreach (var book in books.OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase))
        {
            var entry = new XElement(Atom + "entry",
                new XElement(Atom + "id", $"urn:shelf:book:{book.Id}"),
                new XElement(Atom + "title", book.Title),
                new XElement(Atom + "author", new XElement(Atom + "name", book.Author)),
                new XElement(Atom + "updated", book.AddedAt.ToString("o")),
                Link("http://opds-spec.org/acquisition", $"/opds/books/{book.Id}/file", MediaType(book.EbookStoredName)));
            foreach (var format in formats?[book.Id] ?? [])
            {
                entry.Add(Link("http://opds-spec.org/acquisition", $"/opds/books/{book.Id}/formats/{format.Id}", EbookStore.ContentType(format.StoredName)));
            }

            if (book.Language is { } language)
            {
                entry.Add(new XElement(Dc + "language", language));
            }

            if (book.Year is int year)
            {
                entry.Add(new XElement(Dc + "issued", year));
            }

            if (book.Subtitle is { } subtitle)
            {
                entry.Add(new XElement(Atom + "summary", subtitle));
            }

            if (book.CoverUrl is { } cover)
            {
                entry.Add(Link("http://opds-spec.org/image", cover, "image/jpeg"));
                entry.Add(Link("http://opds-spec.org/image/thumbnail", cover, "image/jpeg"));
            }

            feed.Add(entry);
        }

        return feed;
    }

    private static XElement Feed(string id, string title, string self, string type) =>
        new(Atom + "feed",
            new XAttribute(XNamespace.Xmlns + "dc", Dc),
            new XElement(Atom + "id", id),
            new XElement(Atom + "title", title),
            new XElement(Atom + "updated", DateTimeOffset.UtcNow.ToString("o")),
            new XElement(Atom + "author", new XElement(Atom + "name", "Shelf")),
            Link("self", self, type),
            Link("start", "/opds", Navigation));

    private static XElement Link(string rel, string href, string type) =>
        new(Atom + "link", new XAttribute("rel", rel), new XAttribute("href", href), new XAttribute("type", type));

    private static string MediaType(string? storedName) =>
        EbookStore.ContentType(storedName);

    private static IResult Xml(XElement feed, string type) =>
        Results.Text(new XDocument(new XDeclaration("1.0", "utf-8", null), feed).Declaration + "\n" + feed, type + ";charset=utf-8");
}

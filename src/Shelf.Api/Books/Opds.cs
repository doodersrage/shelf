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
        opds.MapGet("/books/{id:int}/file", File);
    }

    private static IResult Root(ShelfDb db)
    {
        var feed = Feed("urn:shelf:opds", "Shelf", "/opds", Navigation);
        foreach (var (id, title, href, text) in new[]
        {
            ("reading", "Reading", "/opds/books?status=Reading", "The e-books you are reading."),
            ("want", "Want to read", "/opds/books?status=Want", "The e-books on your want list."),
            ("finished", "Finished", "/opds/books?status=Finished", "The e-books you have finished."),
            ("all", "All e-books", "/opds/books", "Every e-book on your shelf."),
            ("borrowed", "Lent to you", "/opds/borrowed", "E-books other readers have lent you."),
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
        var title = status is { } chosen ? BookRules.StatusLabel(chosen) : "All e-books";
        return Xml(Acquire($"urn:shelf:opds:books:{status}", title, status is null ? "/opds/books" : $"/opds/books?status={status}", books), Acquisition);
    }

    private static async Task<IResult> Borrowed(ShelfDb db, CancellationToken cancellationToken)
    {
        var readerId = db.ReaderId;
        var books = await db.Books.IgnoreQueryFilters().AsNoTracking()
            .Where(book => readerId != 0 && book.BorrowerId == readerId && book.EbookStoredName != null)
            .ToListAsync(cancellationToken);
        return Xml(Acquire("urn:shelf:opds:borrowed", "Lent to you", "/opds/borrowed", books), Acquisition);
    }

    private static async Task<IResult> File(int id, ShelfDb db, EbookStore store, CancellationToken cancellationToken)
    {
        var book = (await Lending.OpenAsync(db, id, cancellationToken))?.Book;
        var path = book is null ? null : store.OpenPath(book.EbookStoredName);
        if (book is null || path is null)
        {
            return TypedResults.NotFound();
        }

        return Results.File(path, MediaType(book.EbookStoredName), book.EbookFileName ?? Path.GetFileName(path), enableRangeProcessing: true);
    }

    private static XElement Acquire(string id, string title, string self, IEnumerable<Book> books)
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

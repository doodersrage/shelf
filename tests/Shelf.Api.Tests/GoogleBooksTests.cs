using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class GoogleBooksTests
{
    private const string Volume = """
        { "totalItems": 1, "items": [ { "volumeInfo": {
            "title": "La mano izquierda de la oscuridad", "subtitle": "Novela", "authors": ["Ursula K. Le Guin"],
            "publisher": "Minotauro", "publishedDate": "2014-05-06", "pageCount": 352, "language": "es",
            "categories": ["Fiction"], "industryIdentifiers": [ { "type": "ISBN_10", "identifier": "8445076752" }, { "type": "ISBN_13", "identifier": "9788445076750" } ],
            "imageLinks": { "smallThumbnail": "http://books.google.com/books/content?id=abc&printsec=frontcover&img=1&zoom=5&edge=curl&source=gbs_api",
                            "thumbnail": "http://books.google.com/books/content?id=abc&printsec=frontcover&img=1&zoom=1&edge=curl&source=gbs_api" } } } ] }
        """;

    [Fact]
    public async Task A_book_open_library_does_not_know_comes_from_google_books()
    {
        var (lookup, google, _) = Make(openLibrary: _ => NotFound(), google: _ => Json(Volume));
        var match = await lookup.FindAsync("978-84-450-7675-0", null, null, CancellationToken.None);
        Assert.NotNull(match);
        Assert.Equal(("La mano izquierda de la oscuridad", "Novela", "Ursula K. Le Guin", "Minotauro", 2014, 352, "Spanish"),
            (match!.Title, match.Subtitle, match.Author, match.Publisher, match.Year, match.Pages, match.Language));
        Assert.Equal("https://books.google.com/books/content?id=abc&printsec=frontcover&img=1&zoom=1&source=gbs_api", match.CoverUrl);
        Assert.Equal("9788445076750", match.Isbn);
        Assert.Contains("q=isbn%3A9788445076750", Assert.Single(google.Asked));
    }

    [Fact]
    public async Task Open_library_comes_first_and_google_books_fills_only_what_it_left_blank()
    {
        var (lookup, google, _) = Make(
            openLibrary: path => path.StartsWith("isbn/", StringComparison.Ordinal)
                ? Json("""{ "title": "The Left Hand of Darkness", "languages": [ { "key": "/languages/eng" } ] }""")
                : Json("""{ "docs": [ { "title": "The Left Hand of Darkness", "author_name": ["Ursula K. Le Guin"], "first_publish_year": 1969 } ] }"""),
            google: _ => Json(Volume));
        var match = await lookup.FindAsync("9780441478125", null, null, CancellationToken.None);
        Assert.Equal(("The Left Hand of Darkness", 1969, "English", "9780441478125"), (match!.Title, match.Year, match.Language, match.Isbn));
        // Open Library had no pages, publisher, or cover; those come from Google.
        Assert.Equal((352, "Minotauro"), (match.Pages, match.Publisher));
        Assert.StartsWith("https://books.google.com/", match.CoverUrl);
        Assert.Single(google.Asked);
    }

    [Fact]
    public async Task Google_books_is_not_asked_when_open_library_has_it_all_or_when_it_is_turned_off()
    {
        var complete = (string path) => path.StartsWith("isbn/", StringComparison.Ordinal)
            ? Json("""{ "title": "The Left Hand of Darkness", "number_of_pages": 304, "publishers": ["Ace"], "covers": [12345] }""")
            : Json("""{ "docs": [ { "title": "The Left Hand of Darkness", "author_name": ["Ursula K. Le Guin"], "first_publish_year": 1969 } ] }""");
        var (lookup, google, _) = Make(openLibrary: complete, google: _ => Json(Volume));
        Assert.Equal(304, (await lookup.FindAsync("9780441478125", null, null, CancellationToken.None))!.Pages);
        Assert.Empty(google.Asked);

        var (off, quiet, _) = Make(openLibrary: _ => NotFound(), google: _ => Json(Volume), enabled: false);
        Assert.Null(await off.FindAsync("9788445076750", null, null, CancellationToken.None));
        Assert.Empty(quiet.Asked);

        // Google over its daily allowance answers 429; that is no match, not a failure.
        var (busy, _, _) = Make(openLibrary: _ => NotFound(), google: _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        Assert.Null(await busy.FindAsync("9788445076750", null, null, CancellationToken.None));
    }

    private static (CatalogLookup Lookup, Answering Google, Answering OpenLibrary) Make(Func<string, HttpResponseMessage> openLibrary, Func<string, HttpResponseMessage> google, bool enabled = true)
    {
        var openLibraryHandler = new Answering(openLibrary);
        var googleHandler = new Answering(google);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Lookup:GoogleBooks"] = enabled ? "true" : "false" }).Build();
        var lookup = new CatalogLookup(
            new OpenLibraryLookup(new HttpClient(openLibraryHandler) { BaseAddress = new Uri("https://openlibrary.org/") }),
            new GoogleBooksLookup(new HttpClient(googleHandler) { BaseAddress = new Uri("https://www.googleapis.com/") }, configuration));
        return (lookup, googleHandler, openLibraryHandler);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };

    private sealed class Answering(Func<string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            Asked.Add(path);
            return Task.FromResult(answer(path));
        }
    }
}

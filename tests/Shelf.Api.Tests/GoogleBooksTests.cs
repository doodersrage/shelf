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

    [Fact]
    public async Task Hardcover_gives_the_series_and_what_else_the_others_leave_blank()
    {
        var hardcover = new Hardcover(new CatalogMatch("The Fifth Season", "N. K. Jemisin", 2015, 468, "Orbit", "English", "https://assets.hardcover.app/fifth.jpg", Series: "The Broken Earth", SeriesNumber: 1));
        var (lookup, _, _) = Make(openLibrary: _ => NotFound(), google: _ => Json(Volume), hardcover: hardcover);
        var match = (await lookup.FindAsync("9788445076750", null, null, CancellationToken.None))!;
        Assert.Equal("The Broken Earth", match.Series);
        Assert.Equal(1, match.SeriesNumber);
        // Google's answers stand where it had them.
        Assert.NotEqual("The Fifth Season", match.Title);
        Assert.Single(hardcover.Asked);

        // Without Hardcover connected, as before.
        var (plain, _, _) = Make(openLibrary: _ => NotFound(), google: _ => Json(Volume));
        Assert.Null((await plain.FindAsync("9788445076750", null, null, CancellationToken.None))!.Series);
    }

    // Nothing found because no catalog could be asked is told apart from nothing found: a catalog that answers "not
    // found" has answered, and only when every one asked was out of reach, busy, or in trouble could none be asked.
    [Fact]
    public async Task A_lookup_tells_catalogs_out_of_reach_from_a_book_none_knows()
    {
        static HttpResponseMessage Down() => new(HttpStatusCode.ServiceUnavailable);
        static HttpResponseMessage Busy() => new(HttpStatusCode.TooManyRequests);

        var (bothDown, _, _) = Make(_ => Down(), _ => Busy());
        Assert.Null(await bothDown.FindAsync("9780000000002", null, null, CancellationToken.None));
        Assert.True(bothDown.CouldNotAsk);

        var (unknown, _, _) = Make(_ => NotFound(), _ => Json("{\"totalItems\":0}"));
        Assert.Null(await unknown.FindAsync("9780000000002", null, null, CancellationToken.None));
        Assert.False(unknown.CouldNotAsk);

        // Google answered, though Open Library couldn't: the book is simply not known.
        var (googleAnswered, _, _) = Make(_ => Down(), _ => Json("{\"totalItems\":0}"));
        Assert.Null(await googleAnswered.FindAsync("9780000000002", null, null, CancellationToken.None));
        Assert.False(googleAnswered.CouldNotAsk);

        // With Google Books turned off, Open Library out of reach is every catalog there is.
        var (alone, _, _) = Make(_ => Down(), _ => Json("{}"), enabled: false);
        Assert.Null(await alone.FindAsync("9780000000002", null, null, CancellationToken.None));
        Assert.True(alone.CouldNotAsk);

        // The details review asks both catalogs apart, and tells the same.
        static GoogleBooksLookup Google(Func<string, HttpResponseMessage> answer) => new(new HttpClient(new Answering(answer)) { BaseAddress = new Uri("https://www.googleapis.com/") },
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Lookup:GoogleBooks"] = "true" }).Build());
        var reviewDown = new CatalogSources(new OpenLibraryLookup(new HttpClient(new Answering(_ => Down())) { BaseAddress = new Uri("https://openlibrary.org/") }), Google(_ => Busy()));
        Assert.Equal((null, null), await reviewDown.BothAsync("9780000000002", null, null, CancellationToken.None));
        Assert.True(reviewDown.CouldNotAsk);
        var reviewAnswered = new CatalogSources(new OpenLibraryLookup(new HttpClient(new Answering(_ => Down())) { BaseAddress = new Uri("https://openlibrary.org/") }), Google(_ => Json("{\"totalItems\":0}")));
        await reviewAnswered.BothAsync("9780000000002", null, null, CancellationToken.None);
        Assert.False(reviewAnswered.CouldNotAsk);

        // Find's search the same.
        var search = new OpenLibrarySearch(new HttpClient(new Answering(_ => Down())) { BaseAddress = new Uri("https://openlibrary.org/") });
        Assert.Empty(await search.SearchAsync("moby dick", CancellationToken.None));
        Assert.True(search.CouldNotAsk);
        var answered = new OpenLibrarySearch(new HttpClient(new Answering(_ => Json("{\"docs\":[]}"))) { BaseAddress = new Uri("https://openlibrary.org/") });
        Assert.Empty(await answered.SearchAsync("no such book anywhere", CancellationToken.None));
        Assert.False(answered.CouldNotAsk);
    }

    private sealed class Hardcover(CatalogMatch? match) : IHardcoverLookup
    {
        public List<string?> Asked { get; } = [];

        public Task<CatalogMatch?> FindAsync(string? isbn, string? title, string? author, CancellationToken cancellationToken)
        {
            Asked.Add(isbn ?? title);
            return Task.FromResult(match);
        }
    }

    private static (CatalogLookup Lookup, Answering Google, Answering OpenLibrary) Make(Func<string, HttpResponseMessage> openLibrary, Func<string, HttpResponseMessage> google, bool enabled = true, IHardcoverLookup? hardcover = null)
    {
        var openLibraryHandler = new Answering(openLibrary);
        var googleHandler = new Answering(google);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Lookup:GoogleBooks"] = enabled ? "true" : "false" }).Build();
        var lookup = new CatalogLookup(
            new OpenLibraryLookup(new HttpClient(openLibraryHandler) { BaseAddress = new Uri("https://openlibrary.org/") }),
            new GoogleBooksLookup(new HttpClient(googleHandler) { BaseAddress = new Uri("https://www.googleapis.com/") }, configuration),
            hardcover ?? new Hardcover(null));
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

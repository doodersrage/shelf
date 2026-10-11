using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class AudibleTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly string Fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "e2e", "fixtures"));

    [Fact]
    public async Task A_match_fills_the_details_and_names_the_players_chapters_across_tracks()
    {
        var client = await factory.SignUpAsync("Audible Listener");
        var book = (await (await client.PostAsJsonAsync("/books", new CreateBookRequest("Three Tracks", "Someone", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        using (var content = new MultipartFormDataContent { { new StreamContent(File.OpenRead(Path.Combine(Fixtures, "three-tracks.zip"))), "file", "three-tracks.zip" } })
        {
            (await client.PostAsync($"/books/{book.Id}/audio", content)).EnsureSuccessStatusCode();
        }

        var found = await client.GetFromJsonAsync<AudibleMatch[]>($"/books/{book.Id}/audible?region=uk", JsonOptions);
        Assert.Equal(("B0TESTASIN", "uk"), (Assert.Single(found!).Asin, factory.Audible.Regions.Last()));

        var applied = await (await client.PostAsJsonAsync($"/books/{book.Id}/audible", new AudibleApplyRequest("B0TESTASIN", "uk"), JsonOptions)).Content.ReadFromJsonAsync<AudibleApplied>(JsonOptions);
        Assert.Equal(new AudibleApplied(true, 4, false), applied);

        var after = await client.GetFromJsonAsync<BookResponse>($"/books/{book.Id}", JsonOptions);
        Assert.Equal(("Kate Reading", "The Tracks", 2, 2019), (after!.Narrator, after.Series, after.SeriesNumber, after.Year));
        Assert.Equal("Three Tracks", after.Title);

        // Chapters from the start of the recording land in the track they fall in: about 3, 4, and 5 seconds long.
        var plan = await client.GetFromJsonAsync<AudioPlan>($"/books/{book.Id}/audio/plan", JsonOptions);
        var lengths = plan!.Tracks.Select(track => track.Length!.Value).ToArray();
        Assert.Equal(
            [("Opening Credits", 0, 0.0), ("One", 0, 2.5), ("Two", 1, Math.Round(5 - lengths[0], 3)), ("Three", 2, Math.Round(9.5 - lengths[0] - lengths[1], 3))],
            plan.Chapters.Select(chapter => (chapter.Title, chapter.Track, Math.Round(chapter.Start, 3))));

        // Another reader cannot match a book that is not theirs.
        var stranger = await factory.SignUpAsync("Audible Stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"/books/{book.Id}/audible", new AudibleApplyRequest("B0TESTASIN"), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/books/{book.Id}/audible", new AudibleApplyRequest("not an asin"), JsonOptions)).StatusCode);
    }

    [Fact]
    public void Chapters_past_the_end_of_the_recording_are_another_edition_and_not_used()
    {
        var json = JsonSerializer.Serialize(new[] { new AudibleChapter("One", 0), new AudibleChapter("Two", 30) });
        Assert.Null(Audible.Place(json, [10.0, 10.0]));
        Assert.Null(Audible.Place(json, [40.0, null]));
        Assert.Equal(2, Audible.Place(json, [40.0])!.Length);
        Assert.Null(Audible.Place("not json", [40.0]));
    }

    [Fact]
    public async Task The_catalog_reads_audibles_products_and_audnexus_chapters()
    {
        var asked = new ConcurrentQueue<string>();
        var handler = new Answers(request =>
        {
            asked.Enqueue(request.RequestUri!.AbsoluteUri);
            return request.RequestUri.Host == "api.audnex.us"
                ? """{"chapters":[{"title":"Opening Credits","startOffsetMs":0},{"title":"Chapter 1","startOffsetMs":21500}]}"""
                : """
                  {"products":[{"asin":"B08G9PRS1K","title":"Project Hail Mary","authors":[{"name":"Andy Weir"}],"narrators":[{"name":"Ray Porter"}],
                  "publisher_name":"Audible Studios","release_date":"2021-05-04","language":"english","runtime_length_min":970,
                  "series":[{"title":"Hail Mary","sequence":"1"}],"product_images":{"500":"https://m.media-amazon.com/images/I/cover.jpg"}},
                  {"title":"No ASIN"}]}
                  """;
        });
        var catalog = new AudibleCatalog(new HttpClient(handler));

        var match = Assert.Single(await catalog.SearchAsync("Project Hail Mary", "Andy Weir", "de", CancellationToken.None));
        Assert.Equal(new AudibleMatch("B08G9PRS1K", "Project Hail Mary", null, "Andy Weir", "Ray Porter", "Hail Mary", "1", "Audible Studios", 2021, "English", 970, "https://m.media-amazon.com/images/I/cover.jpg"), match);
        Assert.StartsWith("https://api.audible.de/1.0/catalog/products?", asked.First(), StringComparison.Ordinal);
        Assert.Contains("author=Andy%20Weir", asked.First(), StringComparison.Ordinal);

        Assert.Equal([new AudibleChapter("Opening Credits", 0), new AudibleChapter("Chapter 1", 21.5)], await catalog.ChaptersAsync("B08G9PRS1K", "de", CancellationToken.None));
        Assert.Equal("https://api.audnex.us/books/B08G9PRS1K/chapters?region=de", asked.Last());

        // Covers come only from Amazon's picture servers.
        Assert.Null(await catalog.ImageAsync("https://example.com/cover.jpg", CancellationToken.None));
        Assert.Null(await catalog.ImageAsync("http://m.media-amazon.com/cover.jpg", CancellationToken.None));
        Assert.Empty(await catalog.ChaptersAsync("../etc", "us", CancellationToken.None));
    }

    private sealed class Answers(Func<HttpRequestMessage, string> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer(request), Encoding.UTF8, "application/json") });
    }
}

public sealed class StubAudible : IAudibleCatalog
{
    private static readonly AudibleMatch Match = new("B0TESTASIN", "Three Tracks", null, "Someone", "Kate Reading", "The Tracks", "2", "Test House", 2019, "English", 1, null);

    public ConcurrentQueue<string> Regions { get; } = new();

    public Task<IReadOnlyList<AudibleMatch>> SearchAsync(string title, string? author, string region, CancellationToken cancellationToken)
    {
        Regions.Enqueue(region);
        return Task.FromResult<IReadOnlyList<AudibleMatch>>(title == "Three Tracks" ? [Match] : []);
    }

    public Task<AudibleMatch?> FindAsync(string asin, string region, CancellationToken cancellationToken) =>
        Task.FromResult(asin == Match.Asin ? Match : null);

    public Task<IReadOnlyList<AudibleChapter>> ChaptersAsync(string asin, string region, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AudibleChapter>>([new("Opening Credits", 0), new("One", 2.5), new("Two", 5), new("Three", 9.5)]);

    public Task<byte[]?> ImageAsync(string url, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
}

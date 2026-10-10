using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class SeriesAlertTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task A_newer_book_in_a_series_being_read_is_found_once()
    {
        var reader = await factory.SignUpAsync("Series Reader");
        await reader.PostAsJsonAsync("/books", new CreateBookRequest("All Systems Red", "Martha Wells", BookStatus.Finished, null, Year: 2017, Series: "The Murderbot Diaries", SeriesNumber: 1), JsonOptions);
        await reader.PostAsJsonAsync("/books", new CreateBookRequest("Network Effect", "Martha Wells", BookStatus.Reading, null, Year: 2020, Series: "The Murderbot Diaries", SeriesNumber: 5), JsonOptions);
        // A series only wanted is not followed.
        await reader.PostAsJsonAsync("/books", new CreateBookRequest("A Wizard of Earthsea", "Ursula K. Le Guin", BookStatus.Want, null, Year: 1968, Series: "Earthsea"), JsonOptions);
        factory.SeriesCatalog.Books[("The Murderbot Diaries", "Martha Wells")] =
        [
            new("/works/OL1W", "System Collapse", 2023),
            new("/works/OL2W", "Murderbot Diaries Vol. 3", 2025),
            new("/works/OL3W", "Network Effect", 2020),
            new("/works/OL4W", "Rogue Protocol", 2018),
            new("/works/OL5W", "Fugitive Telemetry", 2021),
        ];

        var first = await (await reader.PostAsync("/books/series/alerts/check", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, first.GetProperty("found").GetInt32());
        Assert.DoesNotContain(factory.SeriesCatalog.Asked, asked => asked.Series == "Earthsea");
        var alerts = await reader.GetFromJsonAsync<SeriesAlertResponse[]>("/books/series/alerts", JsonOptions);
        // Newest first; the omnibus, the one on the shelf, and the older one are left out.
        Assert.Equal(["System Collapse", "Fugitive Telemetry"], alerts!.Select(alert => alert.Title));

        // Looking again finds nothing it found before.
        var again = await (await reader.PostAsync("/books/series/alerts/check", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, again.GetProperty("found").GetInt32());

        // Wanting one puts it on the shelf after the last in the series; dismissing lets the other go.
        var want = await reader.PostAsync($"/books/series/alerts/{alerts![0].Id}/want", null);
        Assert.Equal(HttpStatusCode.Created, want.StatusCode);
        var book = await reader.GetFromJsonAsync<BookResponse>(want.Headers.Location!.ToString(), JsonOptions);
        Assert.Equal(("System Collapse", BookStatus.Want, "The Murderbot Diaries", 6, 2023), (book!.Title, book.Status, book.Series, book.SeriesNumber, book.Year));
        Assert.Equal(HttpStatusCode.NoContent, (await reader.DeleteAsync($"/books/series/alerts/{alerts[1].Id}")).StatusCode);
        Assert.Empty((await reader.GetFromJsonAsync<SeriesAlertResponse[]>("/books/series/alerts", JsonOptions))!);

        // Another reader sees none of them.
        var other = await factory.SignUpAsync("Seriesless Reader");
        Assert.Empty((await other.GetFromJsonAsync<SeriesAlertResponse[]>("/books/series/alerts", JsonOptions))!);
    }
}

public sealed class StubSeriesCatalog : ISeriesCatalog
{
    public ConcurrentDictionary<(string Series, string Author), SeriesEntry[]> Books { get; } = new();

    public ConcurrentBag<(string Series, string Author)> Asked { get; } = [];

    public Task<IReadOnlyList<SeriesEntry>?> FindAsync(string series, string author, CancellationToken cancellationToken)
    {
        Asked.Add((series, author));
        return Task.FromResult<IReadOnlyList<SeriesEntry>?>(Books.TryGetValue((series, author), out var found) ? found : []);
    }
}

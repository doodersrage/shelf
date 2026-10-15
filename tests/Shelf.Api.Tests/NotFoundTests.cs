using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

// An address with nothing at it: a browser is shown Shelf's own page, with somewhere to go; the API keeps its 404.
public sealed class NotFoundTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static async Task<(HttpStatusCode Status, string Page)> BrowseAsync(HttpClient client, string address)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");
        using var response = await client.SendAsync(request);
        return (response.StatusCode, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task A_browser_is_shown_where_it_may_have_meant_to_go()
    {
        var reader = await factory.SignUpAsync("Lost Wanderer");
        var book = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("The Farthest Shore", "Ursula K. Le Guin", BookStatus.Want, null), JsonOptions))
            .Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;

        // A page a letter off.
        var (status, page) = await BrowseAsync(reader, "/libary");
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Contains("There's nothing at /libary.", page);
        Assert.Contains("href=\"/library\"", page);
        Assert.Contains("Perhaps you were looking for", page);
        Assert.Equal(HttpStatusCode.NotFound, (await BrowseAsync(reader, "/authros")).Status);
        Assert.Contains("href=\"/authors\"", (await BrowseAsync(reader, "/authros")).Page);

        // A book by the words of its address, and one by the API's address for it.
        var (_, words) = await BrowseAsync(reader, "/farthest-shore");
        Assert.Contains($"href=\"/library/{book.Id}\"", words);
        Assert.Contains("name=\"q\" value=\"farthest shore\"", words);
        Assert.Contains($"href=\"/library/{book.Id}\"", (await BrowseAsync(reader, $"/book/{book.Id}")).Page);

        // Nothing like anything: still a search, and the way home.
        var (_, nothing) = await BrowseAsync(reader, "/zzqx/9999");
        Assert.DoesNotContain("Perhaps you were looking for", nothing);
        Assert.Contains("action=\"/library\"", nothing);
    }

    [Fact]
    public async Task Signed_out_it_asks_to_sign_in_and_tells_nothing_of_any_shelf()
    {
        var (status, page) = await BrowseAsync(factory.CreateClient(), "/libary");
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Contains("href=\"/signin\"", page);
        Assert.DoesNotContain("Perhaps you were looking for", page);
    }

    [Fact]
    public async Task The_api_and_anything_not_asking_for_a_page_keep_a_plain_404()
    {
        await factory.SignUpAsync("Api Caller");
        var missing = await factory.Client.GetAsync("/books/987654");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.DoesNotContain("<html", await missing.Content.ReadAsStringAsync());

        // An API answer is never swapped for a page, even when a browser asks for it.
        var (status, page) = await BrowseAsync(factory.Client, "/books/987654");
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.DoesNotContain("<html", page);

        var unknown = await factory.Client.GetAsync("/no/such/thing");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.DoesNotContain("<html", await unknown.Content.ReadAsStringAsync());
    }
}

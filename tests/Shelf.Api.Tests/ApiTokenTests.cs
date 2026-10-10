using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shelf.Api.Books;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class ApiTokenTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task A_token_opens_the_readers_books_and_nothing_of_the_account()
    {
        var reader = await factory.SignUpAsync("Token Reader");
        await reader.PostAsJsonAsync("/books", new CreateBookRequest("The Word for World Is Forest", "Ursula K. Le Guin", BookStatus.Reading, null), JsonOptions);
        var made = await (await reader.PostAsJsonAsync("/account/tokens", new NewApiTokenRequest("Home Assistant", CanChange: true), JsonOptions))
            .Content.ReadFromJsonAsync<NewApiToken>(JsonOptions);
        Assert.StartsWith(ApiTokens.Prefix, made!.Token);

        var script = Bearer(made.Token);
        var books = await script.GetFromJsonAsync<BookResponse[]>("/books?status=Reading", JsonOptions);
        Assert.Equal(["The Word for World Is Forest"], books!.Select(book => book.Title));

        // It can change books, and upload to them, with no anti-forgery token.
        var added = await script.PostAsJsonAsync("/books", new CreateBookRequest("Always Coming Home", "Ursula K. Le Guin", BookStatus.Want, null), JsonOptions);
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var book = await added.Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        using var cover = new MultipartFormDataContent { { new ByteArrayContent(Png), "file", "cover.png" } };
        Assert.True((await script.PostAsync($"/books/{book!.Id}/cover", cover)).IsSuccessStatusCode);

        // Never the account: not its tokens, not its password, not the admin pages.
        Assert.Equal(HttpStatusCode.Forbidden, (await script.GetAsync("/account/tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await script.PostAsJsonAsync("/account/tokens", new NewApiTokenRequest("More", true), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await script.GetAsync("/admin/readers")).StatusCode);

        // Its last use is noted, and removing it stops it.
        var listed = await reader.GetFromJsonAsync<ApiTokenSummary[]>("/account/tokens", JsonOptions);
        Assert.NotNull(Assert.Single(listed!).LastUsedAt);
        Assert.Equal(HttpStatusCode.NoContent, (await reader.DeleteAsync($"/account/tokens/{made.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await script.GetAsync("/books")).StatusCode);
    }

    [Fact]
    public async Task A_read_only_token_only_reads()
    {
        var reader = await factory.SignUpAsync("Reading Token Reader");
        var made = await (await reader.PostAsJsonAsync("/account/tokens", new NewApiTokenRequest("Dashboard", CanChange: false), JsonOptions))
            .Content.ReadFromJsonAsync<NewApiToken>(JsonOptions);

        var script = Bearer(made!.Token);
        Assert.Equal(HttpStatusCode.OK, (await script.GetAsync("/books/stats")).StatusCode);
        var refused = await script.PostAsJsonAsync("/books", new CreateBookRequest("Not Added", "Someone", BookStatus.Want, null), JsonOptions);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("can only read", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_made_up_token_and_a_nameless_one_are_refused()
    {
        var reader = await factory.SignUpAsync("Doubtful Token Reader");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Bearer(ApiTokens.Prefix + "not-a-real-one").GetAsync("/books")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PostAsJsonAsync("/account/tokens", new NewApiTokenRequest("  ", false), JsonOptions)).StatusCode);
    }

    private HttpClient Bearer(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

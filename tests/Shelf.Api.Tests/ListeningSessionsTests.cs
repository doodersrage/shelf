using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class ListeningSessionsTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // Listening told in pieces goes on one sitting for the device that told it, with where it went in the book;
    // another device makes its own; and another reader sees none of it.
    [Fact]
    public async Task Listening_is_kept_as_sittings_by_device()
    {
        var listener = await factory.SignUpAsync("Session Keeper");
        var book = (await (await listener.PostAsJsonAsync("/books", new CreateBookRequest("Sat With", "Someone", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        using (var content = new MultipartFormDataContent { { new StreamContent(BooksEndpointTests.ZipText("01.mp3", "a track for sessions")), "file", "sat.zip" } })
        {
            await listener.PostAsync($"/books/{book.Id}/audio", content);
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        async Task SayAsync(string agent, double seconds, double listened, bool replayed = false)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/books/{book.Id}/audio/place") { Content = JsonContent.Create(new AudioPlaceRequest(0, seconds, listened, today, Replayed: replayed), options: JsonOptions) };
            request.Headers.UserAgent.ParseAdd(agent);
            (await listener.SendAsync(request)).EnsureSuccessStatusCode();
        }

        const string firefox = "Mozilla/5.0 (X11; Linux x86_64; rv:140.0) Gecko/20100101 Firefox/140.0";
        await SayAsync(firefox, 15, 15);
        await SayAsync(firefox, 30, 15);
        await SayAsync("Mozilla/5.0 (Linux; Android 15) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Mobile Safari/537.36", 45, 15);
        await SayAsync(firefox, 50, 5, replayed: true);

        var sessions = (await listener.GetFromJsonAsync<ListeningSessionView[]>($"/books/listening/sessions?book={book.Id}", JsonOptions))!;
        var desk = Assert.Single(sessions, session => session.Device == "Shelf's player in Firefox on Linux");
        Assert.Equal((30, 0.0, 30.0), (desk.Seconds, desk.FromSeconds!.Value, desk.ToSeconds!.Value));
        Assert.Single(sessions, session => session.Device == "Shelf's player in Chrome on Android");
        Assert.Single(sessions, session => session.Device == "Listening offline");
        Assert.Equal("Sat With", sessions[0].Title);

        var stranger = await factory.SignUpAsync("Session Stranger");
        Assert.Empty((await stranger.GetFromJsonAsync<ListeningSessionView[]>("/books/listening/sessions", JsonOptions))!);
    }

    [Theory]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1", "Shelf's player in Safari on iPhone")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Safari/537.36 Edg/141.0", "Shelf's player in Edge on Windows")]
    [InlineData("", "Shelf's player")]
    public void A_browser_is_named_without_telling_one_device_from_another(string agent, string named) =>
        Assert.Equal(named, ListeningSessions.Describe(agent));
}

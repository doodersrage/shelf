using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Shelf.Api.Books;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class AbsApiTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly string Fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "e2e", "fixtures"));

    [Fact]
    public async Task An_audiobookshelf_app_signs_in_lists_plays_and_keeps_the_place()
    {
        var reader = await factory.SignUpAsync("App Listener");
        var book = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Three Tracks", "Ursula K. Le Guin", BookStatus.Reading, null, Series: "Earthsea", SeriesNumber: 1), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        using (var content = new MultipartFormDataContent { { new StreamContent(File.OpenRead(Path.Combine(Fixtures, "three-tracks.zip"))), "file", "three-tracks.zip" } })
        {
            (await reader.PostAsync($"/books/{book.Id}/audio", content)).EnsureSuccessStatusCode();
        }

        await reader.PostAsJsonAsync("/books", new CreateBookRequest("Paper Only", "Someone", BookStatus.Want, null), JsonOptions);
        var app = factory.CreateClient();

        var status = await app.GetFromJsonAsync<JsonObject>("/status");
        Assert.Equal((true, "2.37.1"), (status!["isInit"]!.GetValue<bool>(), status["serverVersion"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.PostAsJsonAsync("/login", new { username = "App Listener", password = "wrong" })).StatusCode);
        var login = await (await app.PostAsJsonAsync("/login", new { username = "App Listener", password = ShelfApiFactory.Password })).Content.ReadFromJsonAsync<JsonObject>();
        var token = login!["user"]!["accessToken"]!.GetValue<string>();
        Assert.Equal(("shelf", "2.37.1"), (login["userDefaultLibraryId"]!.GetValue<string>(), login["serverSettings"]!["version"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetAsync("/api/libraries")).StatusCode);
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Only books with a file the app can play or read are in the library.
        var items = await app.GetFromJsonAsync<JsonObject>("/api/libraries/shelf/items?limit=10&page=0&minified=1&sort=media.metadata.title");
        var item = Assert.Single(items!["results"]!.AsArray())!;
        Assert.Equal(("Three Tracks", 3, "Earthsea #1"), (item["media"]!["metadata"]!["title"]!.GetValue<string>(), item["media"]!["numTracks"]!.GetValue<int>(), item["media"]!["metadata"]!["seriesName"]!.GetValue<string>()));
        var expanded = await app.GetFromJsonAsync<JsonObject>($"/api/items/{book.Id}?expanded=1&include=progress");
        Assert.Equal(3, expanded!["media"]!["tracks"]!.AsArray().Count);
        var series = await app.GetFromJsonAsync<JsonObject>("/api/libraries/shelf/series");
        Assert.Equal("Earthsea", series!["results"]![0]!["name"]!.GetValue<string>());

        // Playing streams the tracks from the shelf; the place comes back to Shelf.
        var play = await (await app.PostAsJsonAsync($"/api/items/{book.Id}/play", new { deviceInfo = new { deviceId = "phone", clientName = "Abs Android" }, forceDirectPlay = true, mediaPlayer = "exo-player" })).Content.ReadFromJsonAsync<JsonObject>();
        var tracks = play!["audioTracks"]!.AsArray();
        Assert.Equal(3, tracks.Count);
        var second = tracks[1]!;
        var audio = await app.GetAsync(second["contentUrl"]!.GetValue<string>());
        Assert.Equal("audio/mpeg", audio.Content.Headers.ContentType!.MediaType);
        var start = second["startOffset"]!.GetValue<double>();
        Assert.Equal(HttpStatusCode.OK, (await app.PostAsJsonAsync($"/api/session/{play["id"]!.GetValue<string>()}/sync", new { currentTime = start + 2, timeListened = 30, duration = play["duration"]!.GetValue<double>() })).StatusCode);
        var place = await reader.GetFromJsonAsync<PlaceResponse>($"/books/{book.Id}/place", JsonOptions);
        Assert.Equal((1, 2), (place!.AudioTrack, place.AudioSeconds));
        Assert.Equal(30, (await reader.GetFromJsonAsync<ListeningSummary>("/books/listening", JsonOptions))!.TodaySeconds);

        var progress = await app.GetFromJsonAsync<JsonObject>($"/api/me/progress/{book.Id}");
        Assert.InRange(progress!["currentTime"]!.GetValue<double>(), start + 1.5, start + 2.5);
        Assert.Single((await app.GetFromJsonAsync<JsonObject>("/api/me/items-in-progress"))!["libraryItems"]!.AsArray());

        // A bookmark, offline sessions, and finishing the book.
        var mark = await (await app.PostAsJsonAsync($"/api/me/item/{book.Id}/bookmark", new { time = start + 1, title = "Here" })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("Here", mark!["title"]!.GetValue<string>());
        Assert.Single((await app.GetFromJsonAsync<JsonObject>("/api/me"))!["bookmarks"]!.AsArray());
        var local = await (await app.PostAsJsonAsync("/api/session/local-all", new { sessions = new[] { new { id = "local-1", libraryItemId = book.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), currentTime = 1.0, timeListening = 10 } } })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(local!["results"]![0]!["progressSynced"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK, (await app.PatchAsJsonAsync($"/api/me/progress/{book.Id}", new { isFinished = true, currentTime = 10.0 })).StatusCode);
        Assert.Equal(BookStatus.Finished, (await reader.GetFromJsonAsync<BookResponse>($"/books/{book.Id}", JsonOptions))!.Status);

        // The refresh token makes a new access token; a password change ends both.
        var refreshed = await app.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/auth/refresh") { Headers = { { "x-refresh-token", login["user"]!["refreshToken"]!.GetValue<string>() } } });
        Assert.NotNull((await refreshed.Content.ReadFromJsonAsync<JsonObject>())!["user"]!["accessToken"]);
        var stranger = factory.CreateClient();
        stranger.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not a token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task The_apps_reader_and_shelfs_share_the_place_in_an_e_book_to_the_word()
    {
        var reader = await factory.SignUpAsync("App Bookworm");
        var book = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Moby-Dick", "Herman Melville", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        using (var content = new MultipartFormDataContent { { new StreamContent(File.OpenRead(Path.Combine(Fixtures, "moby.epub"))), "file", "moby.epub" } })
        {
            (await reader.PostAsync($"/books/{book.Id}/ebook", content)).EnsureSuccessStatusCode();
        }

        var app = factory.CreateClient();
        var login = await (await app.PostAsJsonAsync("/login", new { username = "App Bookworm", password = ShelfApiFactory.Password })).Content.ReadFromJsonAsync<JsonObject>();
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!["user"]!["accessToken"]!.GetValue<string>());

        // Read in the app: the place comes to Shelf, chapter and all.
        Assert.Equal(HttpStatusCode.OK, (await app.PatchAsJsonAsync($"/api/me/progress/{book.Id}", new { ebookLocation = "epubcfi(/6/4!/4/2/1:12)", ebookProgress = 0.6 })).StatusCode);
        var place = await reader.GetFromJsonAsync<PlaceResponse>($"/books/{book.Id}/place", JsonOptions);
        Assert.Equal(("epubcfi(/6/4!/4/2/1:12)", 1, 0.6), (place!.EbookCfi, place.EbookChapter, place.EbookProgress));

        // And from Shelf back to the app.
        var progress = await app.GetFromJsonAsync<JsonObject>($"/api/me/progress/{book.Id}");
        Assert.Equal(("epubcfi(/6/4!/4/2/1:12)", 0.6), (progress!["ebookLocation"]!.GetValue<string>(), progress["ebookProgress"]!.GetValue<double>()));
        Assert.Equal(HttpStatusCode.OK, (await app.PatchAsJsonAsync($"/api/me/progress/{book.Id}", new { ebookLocation = "javascript:alert(1)" })).StatusCode);
        Assert.Equal("epubcfi(/6/4!/4/2/1:12)", (await reader.GetFromJsonAsync<PlaceResponse>($"/books/{book.Id}/place", JsonOptions))!.EbookCfi);
    }

    [Fact]
    public async Task A_reader_with_two_step_sign_in_signs_in_with_an_api_token()
    {
        var reader = await factory.SignUpAsync("App Careful");
        var readerId = await factory.ReaderIdAsync("App Careful");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Shelf.Api.Data.ShelfDb>();
            var me = db.Readers.Single(item => item.Id == readerId);
            me.TwoFactorEnabled = true;
            await db.SaveChangesAsync();
        }

        NewApiToken made;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            made = (await ApiTokens.CreateAsync(scope.ServiceProvider.GetRequiredService<Shelf.Api.Data.ShelfDb>(), readerId, "Phone", true)).Made!;
        }

        var app = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.PostAsJsonAsync("/login", new { username = "App Careful", password = ShelfApiFactory.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.PostAsJsonAsync("/login", new { username = "App Careful", password = made.Token })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.PostAsJsonAsync("/login", new { username = "Somebody Else", password = made.Token })).StatusCode);
    }

    // An app signed in with an API token stays signed in only while the token does: removing it ends the access and
    // refresh tokens made from it. A token that can only read, or opens only some of the shelf, can't sign an app in,
    // since the app has to play and keep the place. Signing in by password is untouched by any of it.
    [Fact]
    public async Task An_app_signed_in_with_an_api_token_is_signed_out_when_the_token_is_removed()
    {
        await factory.SignUpAsync("App Revoker");
        var readerId = await factory.ReaderIdAsync("App Revoker");
        NewApiToken full, readOnly, narrow;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Shelf.Api.Data.ShelfDb>();
            full = (await ApiTokens.CreateAsync(db, readerId, "Phone", true)).Made!;
            readOnly = (await ApiTokens.CreateAsync(db, readerId, "Read only", false)).Made!;
            narrow = (await ApiTokens.CreateAsync(db, readerId, "Files only", true, areas: [ApiTokens.Files])).Made!;
        }

        var refused = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await refused.PostAsJsonAsync("/login", new { username = "App Revoker", password = readOnly.Token })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await refused.PostAsJsonAsync("/login", new { username = "App Revoker", password = narrow.Token })).StatusCode);

        async Task<(HttpClient App, string Refresh)> SignInAsync(string password)
        {
            var app = factory.CreateClient();
            var login = await (await app.PostAsJsonAsync("/login", new { username = "App Revoker", password })).Content.ReadFromJsonAsync<JsonObject>();
            app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!["user"]!["accessToken"]!.GetValue<string>());
            return (app, login["user"]!["refreshToken"]!.GetValue<string>());
        }

        HttpRequestMessage Refresh(string refresh) => new(HttpMethod.Post, "/auth/refresh") { Headers = { { "x-refresh-token", refresh } } };

        var (byToken, tokenRefresh) = await SignInAsync(full.Token);
        var (byPassword, passwordRefresh) = await SignInAsync(ShelfApiFactory.Password);
        Assert.Equal(HttpStatusCode.OK, (await byToken.GetAsync("/api/libraries")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await byToken.SendAsync(Refresh(tokenRefresh))).StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.True(await ApiTokens.RemoveAsync(scope.ServiceProvider.GetRequiredService<Shelf.Api.Data.ShelfDb>(), readerId, full.Id));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await byToken.GetAsync("/api/libraries")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await byToken.SendAsync(Refresh(tokenRefresh))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await byPassword.GetAsync("/api/libraries")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await byPassword.SendAsync(Refresh(passwordRefresh))).StatusCode);
    }

    // An app marking a book finished without saying a time finishes it and leaves the place where it was, where it
    // once went back to the start of the recording; a book with only an e-book can be finished that way too.
    [Fact]
    public async Task An_app_marking_a_book_finished_keeps_the_place()
    {
        var reader = await factory.SignUpAsync("App Finisher");
        var book = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Three Tracks Done", "Someone", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        using (var content = new MultipartFormDataContent { { new StreamContent(File.OpenRead(Path.Combine(Fixtures, "three-tracks.zip"))), "file", "three-tracks.zip" } })
        {
            (await reader.PostAsync($"/books/{book.Id}/audio", content)).EnsureSuccessStatusCode();
        }

        var paper = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("A Sea Story Done", "Someone", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        using (var content = new MultipartFormDataContent { { new ByteArrayContent(KoreaderHighlightsTests.Epub()), "file", "sea.epub" } })
        {
            (await reader.PostAsync($"/books/{paper.Id}/ebook", content)).EnsureSuccessStatusCode();
        }

        var app = factory.CreateClient();
        var login = await (await app.PostAsJsonAsync("/login", new { username = "App Finisher", password = ShelfApiFactory.Password })).Content.ReadFromJsonAsync<JsonObject>();
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!["user"]!["accessToken"]!.GetValue<string>());
        var play = await (await app.PostAsJsonAsync($"/api/items/{book.Id}/play", new { deviceInfo = new { deviceId = "phone" }, forceDirectPlay = true })).Content.ReadFromJsonAsync<JsonObject>();
        var second = play!["audioTracks"]![1]!["startOffset"]!.GetValue<double>();

        Assert.Equal(HttpStatusCode.OK, (await app.PatchAsJsonAsync($"/api/me/progress/{book.Id}", new { currentTime = second + 1 })).StatusCode);
        var before = (await reader.GetFromJsonAsync<PlaceResponse>($"/books/{book.Id}/place", JsonOptions))!;
        Assert.Equal(1, before.AudioTrack);

        Assert.Equal(HttpStatusCode.OK, (await app.PatchAsJsonAsync($"/api/me/progress/{book.Id}", new { isFinished = true })).StatusCode);
        var after = (await reader.GetFromJsonAsync<PlaceResponse>($"/books/{book.Id}/place", JsonOptions))!;
        Assert.Equal((before.AudioTrack, before.AudioSeconds), (after.AudioTrack, after.AudioSeconds));
        Assert.Equal(BookStatus.Finished, (await reader.GetFromJsonAsync<BookResponse>($"/books/{book.Id}", JsonOptions))!.Status);

        Assert.Equal(HttpStatusCode.OK, (await app.PatchAsJsonAsync($"/api/me/progress/{paper.Id}", new { isFinished = true })).StatusCode);
        Assert.Equal(BookStatus.Finished, (await reader.GetFromJsonAsync<BookResponse>($"/books/{paper.Id}", JsonOptions))!.Status);
    }

    [Fact]
    public async Task The_live_connection_speaks_socket_io_and_signs_in_with_the_token()
    {
        await factory.SignUpAsync("App Socket");
        var app = factory.CreateClient();
        var login = await (await app.PostAsJsonAsync("/login", new { username = "App Socket", password = ShelfApiFactory.Password })).Content.ReadFromJsonAsync<JsonObject>();
        var token = login!["user"]!["accessToken"]!.GetValue<string>();

        var client = factory.Server.CreateWebSocketClient();
        using var socket = await client.ConnectAsync(new Uri(factory.Server.BaseAddress, "/socket.io/?EIO=4&transport=websocket"), CancellationToken.None);
        Assert.StartsWith("0{", await ReceiveAsync(socket), StringComparison.Ordinal);
        await SendAsync(socket, "40");
        Assert.StartsWith("40{", await ReceiveAsync(socket), StringComparison.Ordinal);
        await SendAsync(socket, "42" + new JsonArray("auth", token).ToJsonString());
        var init = await ReceiveAsync(socket);
        Assert.StartsWith("42[\"init\"", init, StringComparison.Ordinal);
        Assert.Contains("App Socket", init, StringComparison.Ordinal);
        await SendAsync(socket, "2");
        Assert.Equal("3", await ReceiveAsync(socket));
        await SendAsync(socket, "42" + new JsonArray("auth", "wrong").ToJsonString());
        Assert.StartsWith("42[\"auth_failed\"", await ReceiveAsync(socket), StringComparison.Ordinal);
    }

    private static Task SendAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<string> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

// Against a stand-in for Audiobookshelf 2.37 that answers as the real server does (its responses were captured from one).
public sealed class AudiobookshelfTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task A_library_comes_across_with_its_files_cover_series_and_place()
    {
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient(AbsClient.ClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeAudiobookshelf())));
        var client = app.CreateClient();
        await ShelfApiFactory.PostFormAsync(client, "/signup", "/account/signup", new() { ["name"] = "Abs Reader", ["password"] = ShelfApiFactory.Password, ["confirm"] = ShelfApiFactory.Password });
        var readerId = await factory.ReaderIdAsync("Abs Reader");
        var http = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient(AbsClient.ClientName);
        var server = AbsClient.ServerAddress("abs.example.org:13378")!;
        Assert.Equal("http://abs.example.org:13378/", server.ToString());
        Assert.Null(AbsClient.ServerAddress("ftp://abs.example.org"));

        // A key that is not turned on, a wrong password, then the real thing either way.
        var refused = await Assert.ThrowsAsync<AbsProblem>(() => AbsClient.ConnectAsync(http, server, "inactive-key", null, null, CancellationToken.None));
        Assert.Contains("turned on", refused.Message);
        await Assert.ThrowsAsync<AbsProblem>(() => AbsClient.ConnectAsync(http, server, null, "root", "wrong", CancellationToken.None));
        var byPassword = await AbsClient.ConnectAsync(http, server, null, "root", "rootpass", CancellationToken.None);
        Assert.Equal("access-token", byPassword.Token);
        var connection = await AbsClient.ConnectAsync(http, server, "good-key", null, null, CancellationToken.None);
        Assert.True(connection.CanDownload);
        Assert.Equal([new AbsLibrary("lib-books", "Audiobooks")], connection.Libraries);

        var items = await AbsClient.ItemsAsync(http, connection, "lib-books", CancellationToken.None);
        Assert.Equal(["A Wizard of Earthsea", "Moby-Dick", "Locked Away"], items.Select(item => item.Title));
        Assert.Equal((2, (string?)null), (items[0].AudioFiles, items[0].EbookFormat));

        var importer = app.Services.GetRequiredService<AbsImporter>();
        var job = importer.Start(readerId, connection, progress: true, items);
        for (var attempt = 0; attempt < 200 && !job.Finished; attempt++)
        {
            await Task.Delay(50);
        }

        Assert.True(job.Finished);
        var lines = job.Lines;
        Assert.Equal(AbsState.Done, lines["li-earthsea"].State);
        Assert.Equal(AbsState.Done, lines["li-moby"].State);
        Assert.Equal(AbsState.Failed, lines["li-locked"].State);
        Assert.Contains("may not download", lines["li-locked"].Note);

        var earthsea = await client.GetFromJsonAsync<BookResponse>($"/books/{lines["li-earthsea"].BookId}", JsonOptions);
        Assert.Equal(("A Wizard of Earthsea", "Ursula K. Le Guin", BookStatus.Reading, "2 tracks"), (earthsea!.Title, earthsea.Author, earthsea.Status, earthsea.AudioFileName));
        Assert.Equal(("Earthsea Cycle", 1), (earthsea.Series, earthsea.SeriesNumber));
        Assert.Equal("Rob Inglis", earthsea.Narrator);
        Assert.Contains("fantasy", earthsea.Tags);
        Assert.Equal(1968, earthsea.Year);
        var place = await client.GetFromJsonAsync<PlaceResponse>($"/books/{earthsea.Id}/place", JsonOptions);
        Assert.Equal((1, 2), (place!.AudioTrack, place.AudioSeconds));
        var player = await client.GetStringAsync($"/library/{earthsea.Id}/listen");
        Assert.True(player.IndexOf("01 - Part 1.mp3", StringComparison.Ordinal) < player.IndexOf("02 - Part 2.mp3", StringComparison.Ordinal));
        Assert.Equal(FakeAudiobookshelf.Cover, await client.GetByteArrayAsync(System.Text.RegularExpressions.Regex.Match(
            await client.GetStringAsync($"/library/{earthsea.Id}"), $"/books/{earthsea.Id}/cover\\?v=[0-9a-f]{{8}}").Value));

        var moby = await client.GetFromJsonAsync<BookResponse>($"/books/{lines["li-moby"].BookId}", JsonOptions);
        Assert.Equal((BookStatus.Finished, "moby.epub"), (moby!.Status, moby.EbookFileName));
        Assert.Equal(new DateOnly(2026, 1, 2), moby.FinishedOn);

        // A second time, everything is on the shelf already.
        var again = importer.Start(readerId, connection, progress: true, items.Take(2).ToList());
        for (var attempt = 0; attempt < 200 && !again.Finished; attempt++)
        {
            await Task.Delay(50);
        }

        Assert.All(again.Lines.Values, line => Assert.Equal(AbsState.Skipped, line.State));
        Assert.Equal(3, AbsClient.SpineIndex("epubcfi(/6/8!/4/2/1:0)"));
        Assert.Null(AbsClient.SpineIndex("not a cfi"));
    }

    private sealed class FakeAudiobookshelf : HttpMessageHandler
    {
        public static readonly byte[] Cover = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        private static readonly byte[] Moby = ImportTests.Epub("Moby-Dick", "Herman Melville", null, "en", cover: false);
        private static readonly byte[][] Tracks = [ImportTests.Mp3("Part 1", null, "Ursula K. Le Guin"), ImportTests.Mp3("Part 2", null, "Ursula K. Le Guin")];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var token = request.Headers.Authorization?.Parameter;
            HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            HttpResponseMessage Bytes(byte[] body, string type) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) { Headers = { ContentType = new(type) } } };

            if (path == "/login")
            {
                var login = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                return login.GetProperty("password").GetString() == "rootpass"
                    ? Json("""{"user":{"id":"u1","username":"root","token":"legacy-token","accessToken":"access-token"}}""")
                    : new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (token is not ("good-key" or "access-token"))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            return path switch
            {
                "/api/authorize" => Json("""{"user":{"id":"u1","username":"root","permissions":{"download":true}}}"""),
                "/api/libraries" => Json("""{"libraries":[{"id":"lib-books","name":"Audiobooks","mediaType":"book"},{"id":"lib-pods","name":"Podcasts","mediaType":"podcast"}]}"""),
                "/api/libraries/lib-books/items" => Json(request.RequestUri.Query.Contains("page=0") ? """
                    {"results":[
                      {"id":"li-earthsea","mediaType":"book","media":{"metadata":{"title":"A Wizard of Earthsea","authorName":"Ursula K. Le Guin"},"numAudioFiles":2,"duration":12}},
                      {"id":"li-moby","mediaType":"book","media":{"metadata":{"title":"Moby-Dick","authorName":"Herman Melville"},"numAudioFiles":0,"ebookFormat":"epub","duration":0}},
                      {"id":"li-locked","mediaType":"book","media":{"metadata":{"title":"Locked Away","authorName":"Someone"},"numAudioFiles":1,"duration":5}}
                    ],"total":3,"limit":200,"page":0}
                    """ : """{"results":[],"total":3}"""),
                "/api/items/li-earthsea" => Json("""
                    {"id":"li-earthsea","media":{"metadata":{"title":"A Wizard of Earthsea","authorName":"Ursula K. Le Guin","narratorName":"Rob Inglis","publishedYear":"1968",
                      "series":[{"id":"s1","name":"Earthsea Cycle","sequence":"1"}],"genres":["Fantasy","Classics"],"isbn":null,"language":"English"},
                      "audioFiles":[
                        {"index":2,"ino":"202","duration":6,"metadata":{"filename":"02 - Part 2.mp3","ext":".mp3"}},
                        {"index":1,"ino":"201","duration":6,"metadata":{"filename":"01 - Part 1.mp3","ext":".mp3"}}],
                      "ebookFile":null}}
                    """),
                "/api/items/li-moby" => Json("""
                    {"id":"li-moby","media":{"metadata":{"title":"Moby-Dick","authorName":"Herman Melville","series":[],"genres":[]},
                      "audioFiles":[],"ebookFile":{"ino":"301","ebookFormat":"epub","metadata":{"filename":"moby.epub","ext":".epub"}}}}
                    """),
                "/api/items/li-locked" => Json("""
                    {"id":"li-locked","media":{"metadata":{"title":"Locked Away","authorName":"Someone"},
                      "audioFiles":[{"index":1,"ino":"401","duration":5,"metadata":{"filename":"locked.mp3"}}],"ebookFile":null}}
                    """),
                "/api/items/li-earthsea/file/201/download" => Bytes(Tracks[0], "audio/mpeg"),
                "/api/items/li-earthsea/file/202/download" => Bytes(Tracks[1], "audio/mpeg"),
                "/api/items/li-moby/file/301/download" => Bytes(Moby, "application/epub+zip"),
                "/api/items/li-locked/file/401/download" => new HttpResponseMessage(HttpStatusCode.Forbidden),
                "/api/items/li-earthsea/cover" => Bytes(Cover, "image/png"),
                "/api/me/progress/li-earthsea" => Json("""{"currentTime":8.5,"duration":12,"progress":0.7,"isFinished":false}"""),
                "/api/me/progress/li-moby" => Json("""{"isFinished":true,"finishedAt":1767355200000,"ebookLocation":"epubcfi(/6/4!/4/2/1:0)"}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Readers;

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
        // The stand-in answers for both clients: an admin's, and the one kept to the internet for other readers.
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient(AbsClient.ClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeAudiobookshelf());
            services.AddHttpClient(AbsClient.ReaderClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeAudiobookshelf());
        }));
        var client = app.CreateClient();
        await ShelfApiFactory.PostFormAsync(client, "/signup", "/account/signup", new() { ["name"] = "Abs Reader", ["email"] = ShelfApiFactory.EmailOf("Abs Reader"), ["password"] = ShelfApiFactory.Password, ["confirm"] = ShelfApiFactory.Password });
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
        var job = importer.Start(readerId, connection, AbsOptions.Place(true), items);
        for (var attempt = 0; attempt < 200 && !job.Finished; attempt++)
        {
            await Task.Delay(50);
        }

        Assert.True(job.Finished);
        var lines = job.Books;
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
        var again = importer.Start(readerId, connection, AbsOptions.Place(true) with { UpdateExisting = false }, items.Take(2).ToList());
        for (var attempt = 0; attempt < 200 && !again.Finished; attempt++)
        {
            await Task.Delay(50);
        }

        Assert.All(again.Books.Values, line => Assert.Equal(AbsState.Skipped, line.State));

        // What came across is remembered, so keeping in step brings only what it has never brought: here the one
        // that could not come across before.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
            var db = scope.ServiceProvider.GetRequiredService<Shelf.Api.Data.ShelfDb>();
            Assert.Equal(["li-earthsea", "li-moby"], db.AbsLinks.Where(link => link.ReaderId == readerId).Select(link => link.ItemId).OrderBy(id => id).ToList());
            var keeper = app.Services.GetRequiredService<AbsKeeper>();
            await keeper.KeepAsync(db, server, "good-key", "lib-books", "Audiobooks", bringProgress: false);
            Assert.DoesNotContain("good-key", db.AbsSyncs.Single(item => item.ReaderId == readerId).SealedKey);
            Assert.Equal(1, await keeper.CheckAsync(db.AbsSyncs.Single(item => item.ReaderId == readerId).Id, CancellationToken.None));
            var kept = await AbsKeeper.StateAsync(db);
            Assert.Equal(("Audiobooks", 1, (string?)null), (kept!.LibraryName, kept.LastFound, kept.Problem));
            var fetched = importer.Latest(readerId)!;
            for (var attempt = 0; attempt < 200 && !fetched.Finished; attempt++)
            {
                await Task.Delay(50);
            }

            Assert.Equal(["li-locked"], fetched.Items.Select(item => item.Id));
            await AbsKeeper.StopAsync(db);
            Assert.Null(await AbsKeeper.StateAsync(db));
        }
        Assert.Equal(3, AbsClient.SpineIndex("epubcfi(/6/8!/4/2/1:0)"));
        Assert.Null(AbsClient.SpineIndex("not a cfi"));
    }

    [Fact]
    public async Task A_readers_bookmarks_history_dates_and_lists_come_across_and_books_already_here_get_their_reading()
    {
        var fake = new FakeState();
        await using var app = Fake(fake);
        var client = app.CreateClient();
        await ShelfApiFactory.PostFormAsync(client, "/signup", "/account/signup", new() { ["name"] = "Abs Lister", ["email"] = ShelfApiFactory.EmailOf("Abs Lister"), ["password"] = ShelfApiFactory.Password, ["confirm"] = ShelfApiFactory.Password });
        var readerId = await factory.ReaderIdAsync("Abs Lister");
        var http = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient(AbsClient.ClientName);
        var connection = await AbsClient.ConnectAsync(http, AbsClient.ServerAddress("abs.example.org")!, "good-key", null, null, CancellationToken.None);
        Assert.True(connection.Admin);
        var items = (await AbsClient.ItemsAsync(http, connection, "lib-books", CancellationToken.None)).Take(2).ToList();
        var importer = app.Services.GetRequiredService<AbsImporter>();
        await FinishAsync(importer.Start(readerId, connection, new AbsOptions(), items));

        int earthseaId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
            var db = scope.ServiceProvider.GetRequiredService<Shelf.Api.Data.ShelfDb>();
            var earthsea = await db.Books.SingleAsync(book => book.Title == "A Wizard of Earthsea");
            earthseaId = earthsea.Id;
            Assert.Equal((BookStatus.Reading, new DateOnly(2026, 1, 1), 1, 2), (earthsea.Status, earthsea.StartedOn, earthsea.AudioTrack, earthsea.AudioSeconds));
            var marks = await db.AudioBookmarks.Where(mark => mark.BookId == earthsea.Id).OrderBy(mark => mark.Track).ToListAsync();
            Assert.Equal([(0, 3, "The shadow"), (1, 4, "Roke")], marks.Select(mark => (mark.Track, mark.Seconds, mark.Note)));
            var days = await db.ListeningDays.IgnoreQueryFilters().Where(day => day.BookId == earthsea.Id).OrderBy(day => day.Day).Select(day => new { day.Day, day.Seconds }).ToListAsync();
            Assert.Equal([(new DateOnly(2026, 1, 1), 900), (new DateOnly(2026, 1, 2), 300)], days.Select(day => (day.Day, day.Seconds)));
            var moby = await db.Books.SingleAsync(book => book.Title == "Moby-Dick");
            Assert.Equal((BookStatus.Finished, new DateOnly(2025, 12, 20), new DateOnly(2026, 1, 2)), (moby.Status, moby.StartedOn, moby.FinishedOn));
            var lists = await db.Collections.Where(item => item.OwnerId == readerId).Include(item => item.Books).OrderBy(item => item.Name).ToListAsync();
            Assert.Equal(["Earthsea and the sea", "Listen next"], lists.Select(item => item.Name));
            Assert.Equal(2, lists[0].Books.Count);
            Assert.Equal([earthsea.Id], lists[1].Books.Select(item => item.BookId));
        }

        // Later: the reader got further, and brings their reading for the books already here. The files do not come
        // again, and the history and bookmarks are not doubled.
        fake.EarthseaTime = 11;
        var update = importer.Start(readerId, connection, new AbsOptions(), items);
        await FinishAsync(update);
        Assert.Equal(AbsState.Done, update.Books["li-earthsea"].State);
        Assert.Contains("your reading came across", update.Books["li-earthsea"].Note);
        Assert.Equal(1, fake.Downloads["li-earthsea"]);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
            var db = scope.ServiceProvider.GetRequiredService<Shelf.Api.Data.ShelfDb>();
            var earthsea = await db.Books.SingleAsync(book => book.Id == earthseaId);
            Assert.Equal((1, 5), (earthsea.AudioTrack, earthsea.AudioSeconds));
            Assert.Equal(2, await db.AudioBookmarks.CountAsync(mark => mark.BookId == earthseaId));
            Assert.Equal(1200, await db.ListeningDays.IgnoreQueryFilters().Where(day => day.BookId == earthseaId).SumAsync(day => day.Seconds));
        }
    }

    [Fact]
    public async Task An_admin_brings_another_users_books_and_reading_to_their_shelf_only_what_they_started()
    {
        var fake = new FakeState();
        await using var app = Fake(fake);
        var client = app.CreateClient();
        await ShelfApiFactory.PostFormAsync(client, "/signup", "/account/signup", new() { ["name"] = "Abs Ged", ["email"] = "ged@example.test", ["password"] = ShelfApiFactory.Password, ["confirm"] = ShelfApiFactory.Password });
        var gedId = await factory.ReaderIdAsync("Abs Ged");
        var http = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient(AbsClient.ClientName);
        var connection = await AbsClient.ConnectAsync(http, AbsClient.ServerAddress("abs.example.org")!, "good-key", null, null, CancellationToken.None);
        var users = await AbsClient.UsersAsync(http, connection, CancellationToken.None);
        Assert.Equal(["ged", "root"], users.Select(user => user.Name));
        Assert.Equal("ged@example.test", users[0].Email);

        var items = await AbsClient.ItemsAsync(http, connection, "lib-books", CancellationToken.None);
        var job = app.Services.GetRequiredService<AbsImporter>().Start(gedId, connection, new AbsOptions(), items, admin: true, absUserId: "u2", forName: "Abs Ged", startedOnly: true);
        await FinishAsync(job);
        Assert.Equal("Brought across from Audiobookshelf for Abs Ged", job.Title(true));
        Assert.Equal(["li-moby"], job.Items.Select(item => item.Id));
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(gedId);
        var db = scope.ServiceProvider.GetRequiredService<Shelf.Api.Data.ShelfDb>();
        var moby = await db.Books.SingleAsync();
        Assert.Equal(("Moby-Dick", BookStatus.Finished), (moby.Title, moby.Status));
        Assert.Equal(gedId, moby.OwnerId);
    }

    private WebApplicationFactory<Program> Fake(FakeState state) => factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
    {
        services.AddHttpClient(AbsClient.ClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeAudiobookshelf(state));
        services.AddHttpClient(AbsClient.ReaderClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeAudiobookshelf(state));
    }));

    private static async Task FinishAsync(AbsImportJob job)
    {
        for (var attempt = 0; attempt < 200 && !job.Finished; attempt++)
        {
            await Task.Delay(50);
        }

        Assert.True(job.Finished);
    }

    // What the stand-in server knows that can change between imports, and what it was asked for.
    private sealed class FakeState
    {
        public double EarthseaTime { get; set; } = 8.5;
        public System.Collections.Concurrent.ConcurrentDictionary<string, int> Downloads { get; } = new();
    }

    private sealed class FakeAudiobookshelf(FakeState? state = null) : HttpMessageHandler
    {
        private readonly FakeState _state = state ?? new FakeState();

        public static readonly byte[] Cover = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        private static readonly byte[] Moby = ImportTests.Epub("Moby-Dick", "Herman Melville", null, "en", cover: false);
        private static readonly byte[][] Tracks = [ImportTests.Mp3("Part 1", null, "Ursula K. Le Guin"), ImportTests.Mp3("Part 2", null, "Ursula K. Le Guin")];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var token = request.Headers.Authorization?.Parameter;
            HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            HttpResponseMessage Bytes(byte[] body, string type) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) { Headers = { ContentType = new(type) } } };
            HttpResponseMessage Counted(string item, HttpResponseMessage response)
            {
                _state.Downloads.AddOrUpdate(item, 1, (_, count) => count + 1);
                return response;
            }

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
                "/api/authorize" => Json("""{"user":{"id":"u1","username":"root","type":"root","permissions":{"download":true}}}"""),
                "/api/me" => Json($$"""
                    {"id":"u1","username":"root","mediaProgress":[
                      {"libraryItemId":"li-earthsea","episodeId":null,"currentTime":{{_state.EarthseaTime.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"isFinished":false,"startedAt":1767225600000},
                      {"libraryItemId":"li-moby","isFinished":true,"startedAt":1766188800000,"finishedAt":1767355200000,"ebookLocation":"epubcfi(/6/4!/4/2/1:0)"}],
                     "bookmarks":[
                      {"libraryItemId":"li-earthsea","time":3.2,"title":"The shadow","createdAt":1767225600000},
                      {"libraryItemId":"li-earthsea","time":10,"title":"Roke","createdAt":1767312000000}]}
                    """),
                "/api/me/listening-sessions" => Json(request.RequestUri.Query.Contains("page=0") ? """
                    {"total":3,"numPages":1,"page":0,"sessions":[
                      {"libraryItemId":"li-earthsea","date":"2026-01-01","timeListening":600},
                      {"libraryItemId":"li-earthsea","date":"2026-01-01","timeListening":300},
                      {"libraryItemId":"li-earthsea","date":"2026-01-02","timeListening":300}]}
                    """ : """{"sessions":[]}"""),
                "/api/collections" => Json("""{"collections":[{"name":"Earthsea and the sea","books":[{"id":"li-earthsea"},{"id":"li-moby"},{"id":"li-elsewhere"}]}]}"""),
                "/api/playlists" => Json("""{"playlists":[{"name":"Listen next","items":[{"libraryItemId":"li-earthsea","episodeId":null}]}]}"""),
                "/api/users" => Json("""{"users":[{"id":"u1","username":"root","type":"root","isActive":true},{"id":"u2","username":"ged","email":"ged@example.test","type":"user","isActive":true},{"id":"u3","username":"gone","type":"user","isActive":false}]}"""),
                "/api/users/u2" => Json("""{"id":"u2","username":"ged","mediaProgress":[{"libraryItemId":"li-moby","isFinished":true,"finishedAt":1767355200000}],"bookmarks":[]}"""),
                "/api/users/u2/listening-sessions" => Json("""{"sessions":[]}"""),
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
                "/api/items/li-earthsea/file/201/download" => Counted("li-earthsea", Bytes(Tracks[0], "audio/mpeg")),
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

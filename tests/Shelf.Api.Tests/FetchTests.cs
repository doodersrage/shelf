using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

// Against stand-ins for Jackett and Deluge (and Prowlarr, qBittorrent, and SABnzbd) that answer as their APIs do.
public sealed class FetchTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly string Fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "e2e", "fixtures"));
    private readonly string downloads = Path.Combine(Path.GetTempPath(), $"shelf-downloads-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(downloads))
        {
            Directory.Delete(downloads, recursive: true);
        }
    }

    private static FetchSetting Settings(FetchKind kind = FetchKind.Ebook) => new() { EbookFormats = "epub,azw3,pdf", AudioFormats = "m4b,mp3", MinSeeders = 1, WantedKind = kind };

    private static Release Release(string title, int seeders = 10, long size = 2_000_000, int[]? categories = null, string protocol = "torrent") =>
        new(Guid.NewGuid().ToString(), title, size, seeders, protocol, "http://indexer.test/dl", null, "Indexer", null, categories ?? [7020], DateTimeOffset.UtcNow);

    [Fact]
    public void Releases_are_weighed_for_being_the_book_and_the_kind_of_file_liked()
    {
        var ranked = FetchRules.Rank(
        [
            Release("Ursula K Le Guin - The Left Hand of Darkness (1969) [PDF]", seeders: 50),
            Release("Ursula K. Le Guin - The Left Hand of Darkness [EPUB]", seeders: 5),
            Release("Le Guin - The Left Hand of Darkness (Audiobook) [M4B]", categories: [3030]),
            Release("The Left Hand of God - Paul Hoffman [EPUB]"),
            Release("Ursula K. Le Guin - The Left Hand of Darkness [EPUB]", seeders: 0),
        ], "The Left Hand of Darkness", "Le Guin, Ursula K.", FetchKind.Ebook, Settings());

        Assert.Equal("Ursula K. Le Guin - The Left Hand of Darkness [EPUB]", ranked[0].Release.Title);
        Assert.Equal(("epub", true), (ranked[0].Format, ranked[0].Matches));
        Assert.True(ranked[1].Matches);
        Assert.Equal("pdf", ranked[1].Format);
        Assert.Equal(2, ranked.Count(item => item.Matches));
        Assert.Contains(ranked, item => item.Why == "it is not an e-book");
        Assert.Contains(ranked, item => item.Why == "the title is not in it");
        Assert.Contains(ranked, item => item.Why == "too few seed it");

        var audio = FetchRules.Rank([Release("Ursula K. Le Guin - The Left Hand of Darkness [M4B]", categories: [3030])], "The Left Hand of Darkness", "Ursula K. Le Guin", FetchKind.Audiobook, Settings());
        Assert.True(audio[0].Matches);
        Assert.Equal("/data/downloads/Book.epub", FetchRules.MapPath("/downloads/Book.epub", new FetchSetting { RemotePath = "/downloads/", LocalPath = "/data/downloads" }));
        Assert.Equal("left hand darkness guin", FetchRules.Search("The Left Hand of Darkness: A Novel", "Ursula K. Le Guin"));
    }

    [Fact]
    public async Task A_wanted_book_is_found_on_jackett_fetched_by_deluge_and_lands_on_the_book()
    {
        var finished = Path.Combine(downloads, "complete");
        Directory.CreateDirectory(Path.Combine(finished, "Ursula K Le Guin - The Left Hand of Darkness"));
        File.Copy(Path.Combine(Fixtures, "moby.epub"), Path.Combine(finished, "Ursula K Le Guin - The Left Hand of Darkness", "The Left Hand of Darkness.epub"));
        var fake = new FakeServices();
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient(ProwlarrClient.ClientName).ConfigurePrimaryHttpMessageHandler(() => fake)));
        var client = app.CreateClient();
        await ShelfApiFactory.PostFormAsync(client, "/signup", "/account/signup", new() { ["name"] = "Fetch Reader", ["password"] = ShelfApiFactory.Password, ["confirm"] = ShelfApiFactory.Password });
        var readerId = await factory.ReaderIdAsync("Fetch Reader");
        var wanted = (await (await client.PostAsJsonAsync("/books", new CreateBookRequest("The Left Hand of Darkness", "Ursula K. Le Guin", BookStatus.Want, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;

        var fetcher = app.Services.GetRequiredService<Fetcher>();
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        db.FetchSettings.RemoveRange(db.FetchSettings);
        db.FetchSettings.Add(new FetchSetting
        {
            Enabled = true,
            IndexerKind = IndexerKind.Jackett,
            IndexerUrl = "http://jackett.test",
            SealedIndexerKey = fetcher.Seal("jackett-key"),
            TorrentKind = TorrentClientKind.Deluge,
            TorrentUrl = "http://deluge.test",
            SealedTorrentPassword = fetcher.Seal("deluge-pass"),
            RemotePath = "/downloads/complete",
            LocalPath = finished,
        });
        await db.SaveChangesAsync();
        var settings = await FetchRules.SettingsAsync(db);
        Assert.Null(await JackettClient.TestAsync(app.Services.GetRequiredService<IHttpClientFactory>().CreateClient(ProwlarrClient.ClientName), "http://jackett.test", "jackett-key", CancellationToken.None));
        Assert.Contains("API key", await JackettClient.TestAsync(app.Services.GetRequiredService<IHttpClientFactory>().CreateClient(ProwlarrClient.ClientName), "http://jackett.test", "wrong", CancellationToken.None));
        Assert.Null(await DelugeClient.TestAsync(app.Services.GetRequiredService<IHttpClientFactory>().CreateClient(ProwlarrClient.ClientName), "http://deluge.test", "deluge-pass", CancellationToken.None));
        Assert.Contains("password", await DelugeClient.TestAsync(app.Services.GetRequiredService<IHttpClientFactory>().CreateClient(ProwlarrClient.ClientName), "http://deluge.test", "wrong", CancellationToken.None));

        // On the want list, with nothing fetched yet; an audiobook wanted is not wanted as an e-book.
        Assert.Contains(await Fetcher.WantedAsync(db, settings), book => book.Id == wanted.Id);
        var listening = (await (await client.PostAsJsonAsync("/books", new CreateBookRequest("The Dispossessed", "Ursula K. Le Guin", BookStatus.Want, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        (await db.Books.FindAsync(listening.Id))!.Format = BookFormat.Audiobook;
        await db.SaveChangesAsync();
        Assert.DoesNotContain(await Fetcher.WantedAsync(db, settings), book => book.Id == listening.Id);
        var found = await fetcher.SearchAsync(settings, wanted.Title, wanted.Author, FetchKind.Ebook, CancellationToken.None);
        Assert.Equal("Ursula K Le Guin - The Left Hand of Darkness (epub)", found[0].Release.Title);
        Assert.True(found[0].Matches);
        Assert.False(found.Single(item => item.Release.Title.Contains("God", StringComparison.Ordinal)).Matches);

        var grab = await fetcher.GrabAsync(db, settings, (await db.Books.FindAsync(wanted.Id))!, FetchKind.Ebook, found[0].Release, CancellationToken.None);
        Assert.Equal((GrabState.Downloading, "abc123def"), (grab.State, grab.DownloadId));
        Assert.Contains("core.add_torrent_file", fake.DelugeCalls);
        Assert.DoesNotContain(await Fetcher.WantedAsync(db, settings), book => book.Id == wanted.Id);

        // Half way, then finished: the file comes onto the book, through the path as Shelf sees it.
        await fetcher.FollowAsync(CancellationToken.None);
        Assert.Equal(0.5, (await NewDb(app, readerId).FetchGrabs.FindAsync(grab.Id))!.Progress);
        fake.DelugeDone = true;
        await fetcher.FollowAsync(CancellationToken.None);
        var landed = (await NewDb(app, readerId).FetchGrabs.FindAsync(grab.Id))!;
        Assert.Equal((GrabState.Done, (string?)null), (landed.State, landed.Problem));
        var book = await client.GetFromJsonAsync<BookResponse>($"/books/{wanted.Id}", JsonOptions);
        Assert.Equal("The Left Hand of Darkness.epub", book!.EbookFileName);
        Assert.Single((await client.GetFromJsonAsync<List<BookResponse>>("/books", JsonOptions))!, item => item.Title == "The Left Hand of Darkness");
        Assert.Contains("Fetched", await client.GetStringAsync($"/library/{wanted.Id}"));
        Assert.Contains("Wanted", await client.GetStringAsync("/wanted"));
    }

    [Fact]
    public async Task Prowlarr_qbittorrent_and_sabnzbd_are_asked_as_their_apis_ask()
    {
        var fake = new FakeServices();
        var http = new HttpClient(fake);
        var releases = await ProwlarrClient.SearchAsync(http, "http://prowlarr.test", "prowlarr-key", "left hand darkness guin", FetchKind.Ebook, CancellationToken.None);
        Assert.Equal(("Le Guin - The Left Hand of Darkness [EPUB]", "usenet", 12), (releases[0].Title, releases[0].Protocol, releases[0].Seeders ?? 12));
        Assert.Null(await ProwlarrClient.TestAsync(http, "http://prowlarr.test", "prowlarr-key", CancellationToken.None));

        await QbitClient.AddAsync(http, "http://qbit.test", "admin", "adminpass", "shelf", 7, null, "magnet:?xt=urn:btih:abc", CancellationToken.None);
        var torrent = await QbitClient.StatusAsync(http, "http://qbit.test", "admin", "adminpass", 7, CancellationToken.None);
        Assert.Equal((true, true, "/downloads/Book"), (torrent.Found, torrent.Finished, torrent.Path));
        Assert.Contains("qBittorrent did not take", await QbitClient.TestAsync(http, "http://qbit.test", "admin", "wrong", CancellationToken.None));

        var id = await SabClient.AddAsync(http, "http://sab.test", "sab-key", "shelf", 8, "<nzb/>"u8.ToArray(), CancellationToken.None);
        Assert.Equal("SABnzbd_nzo_1", id);
        var nzb = await SabClient.StatusAsync(http, "http://sab.test", "sab-key", id, CancellationToken.None);
        Assert.Equal((true, true, "/downloads/complete/Book"), (nzb.Found, nzb.Finished, nzb.Path));
        Assert.Null(await SabClient.TestAsync(http, "http://sab.test", "sab-key", CancellationToken.None));
    }

    [Fact]
    public async Task A_download_cancelled_is_taken_out_of_the_download_client_and_the_book_stays_wanted_but_is_not_looked_for_again()
    {
        var fake = new FakeServices();
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient(ProwlarrClient.ClientName).ConfigurePrimaryHttpMessageHandler(() => fake)));
        var client = app.CreateClient();
        await ShelfApiFactory.PostFormAsync(client, "/signup", "/account/signup", new() { ["name"] = "Cancel Reader", ["password"] = ShelfApiFactory.Password, ["confirm"] = ShelfApiFactory.Password });
        var readerId = await factory.ReaderIdAsync("Cancel Reader");
        var wanted = (await (await client.PostAsJsonAsync("/books", new CreateBookRequest("The Left Hand of Darkness", "Ursula K. Le Guin", BookStatus.Want, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;

        var fetcher = app.Services.GetRequiredService<Fetcher>();
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        db.FetchSettings.RemoveRange(db.FetchSettings);
        db.FetchSettings.Add(new FetchSetting
        {
            Enabled = true,
            IndexerKind = IndexerKind.Jackett,
            IndexerUrl = "http://jackett.test",
            SealedIndexerKey = fetcher.Seal("jackett-key"),
            TorrentKind = TorrentClientKind.Deluge,
            TorrentUrl = "http://deluge.test",
            SealedTorrentPassword = fetcher.Seal("deluge-pass"),
        });
        await db.SaveChangesAsync();
        var settings = await FetchRules.SettingsAsync(db);
        var found = await fetcher.SearchAsync(settings, wanted.Title, wanted.Author, FetchKind.Ebook, CancellationToken.None);
        var grab = await fetcher.GrabAsync(db, settings, (await db.Books.FindAsync(wanted.Id))!, FetchKind.Ebook, found[0].Release, CancellationToken.None);
        Assert.Equal(GrabState.Downloading, grab.State);

        Assert.True(await fetcher.CancelAsync(NewDb(app, readerId), grab.Id, CancellationToken.None));
        Assert.Contains("core.remove_torrent", fake.DelugeCalls);
        var cancelled = (await NewDb(app, readerId).FetchGrabs.FindAsync(grab.Id))!;
        Assert.Equal((GrabState.Cancelled, (string?)null), (cancelled.State, cancelled.Problem));
        Assert.Contains(await Fetcher.WantedAsync(NewDb(app, readerId), settings), book => book.Id == wanted.Id);
        Assert.Contains(wanted.Id, await Fetcher.CancelledAsync(NewDb(app, readerId)));
        Assert.Null(await fetcher.StartWantedAsync(readerId, CancellationToken.None));
        Assert.Contains("not looked for again", await client.GetStringAsync("/wanted"));

        // Once cancelled, it is not followed again, nor cancelled twice.
        fake.DelugeDone = true;
        await fetcher.FollowAsync(CancellationToken.None);
        Assert.Equal(GrabState.Cancelled, (await NewDb(app, readerId).FetchGrabs.FindAsync(grab.Id))!.State);
        Assert.False(await fetcher.CancelAsync(NewDb(app, readerId), grab.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Qbittorrent_and_sabnzbd_are_asked_to_remove_a_download_with_its_files()
    {
        var fake = new FakeServices();
        var http = new HttpClient(fake);
        await QbitClient.RemoveAsync(http, "http://qbit.test", "admin", "adminpass", 7, CancellationToken.None);
        Assert.Contains("hashes=h&deleteFiles=true", fake.Removed);
        await SabClient.RemoveAsync(http, "http://sab.test", "sab-key", "SABnzbd_nzo_1", CancellationToken.None);
        Assert.Contains("queue:SABnzbd_nzo_1", fake.Removed);
        Assert.Contains("history:SABnzbd_nzo_1", fake.Removed);
    }

    private static ShelfDb NewDb(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, int readerId)
    {
        var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
        return scope.ServiceProvider.GetRequiredService<ShelfDb>();
    }

    private sealed class FakeServices : HttpMessageHandler
    {
        public List<string> DelugeCalls { get; } = [];

        public bool DelugeDone { get; set; }

        public List<string> Removed { get; } = [];

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            var query = System.Web.HttpUtility.ParseQueryString(url.Query);
            switch (url.Host)
            {
                case "jackett.test" when query["apikey"] != "jackett-key":
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<error code=\"100\" description=\"Invalid API Key\" />") };
                case "jackett.test" when url.AbsolutePath.EndsWith("/torznab/api", StringComparison.Ordinal):
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<?xml version=\"1.0\"?><caps><server title=\"Jackett\"/></caps>") };
                case "jackett.test" when url.AbsolutePath == "/api/v2.0/indexers/all/results":
                    Assert.Equal("left hand darkness guin", query["Query"]);
                    return Json("""
                        {"Results":[
                          {"Title":"The Left Hand of God - Paul Hoffman (epub)","Guid":"g1","Link":"http://jackett.test/dl/1","Size":1500000,"Seeders":30,"Tracker":"BookTracker","Category":[7020],"PublishDate":"2026-01-01T00:00:00"},
                          {"Title":"Ursula K Le Guin - The Left Hand of Darkness (epub)","Guid":"g2","Link":"http://jackett.test/dl/2","Size":900000,"Seeders":12,"Tracker":"BookTracker","Category":[7020,100001],"PublishDate":"2025-06-01T00:00:00"}
                        ]}
                        """);
                case "jackett.test" when url.AbsolutePath == "/dl/2":
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("d8:announce0:e"u8.ToArray()) };
                case "deluge.test":
                {
                    var call = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                    var method = call["method"]!.GetValue<string>();
                    DelugeCalls.Add(method);
                    if (method != "auth.login" && !request.Headers.TryGetValues("Cookie", out _))
                    {
                        return Json("""{"result":null,"error":{"message":"Not authenticated","code":1},"id":1}""");
                    }

                    var answer = method switch
                    {
                        "auth.login" => call["params"]![0]!.GetValue<string>() == "deluge-pass" ? "true" : "false",
                        "web.connected" => "true",
                        "core.add_torrent_file" => "\"abc123def\"",
                        "core.remove_torrent" => "true",
                        "core.get_torrent_status" => DelugeDone
                            ? """{"progress":100.0,"state":"Seeding","save_path":"/downloads/complete","name":"Ursula K Le Guin - The Left Hand of Darkness","is_finished":true,"message":"OK"}"""
                            : """{"progress":50.0,"state":"Downloading","save_path":"/downloads/incomplete","name":"Ursula K Le Guin - The Left Hand of Darkness","is_finished":false,"message":"OK"}""",
                        _ => null,
                    };
                    var response = answer is null
                        ? Json("""{"result":null,"error":{"message":"Unknown method","code":2},"id":1}""")
                        : Json($$"""{"result":{{answer}},"error":null,"id":1}""");
                    if (method == "auth.login" && answer == "true")
                    {
                        response.Headers.Add("Set-Cookie", "_session_id=s1; Path=/");
                    }

                    return response;
                }

                case "prowlarr.test" when request.Headers.TryGetValues("X-Api-Key", out var keys) && keys.Single() == "prowlarr-key":
                    return url.AbsolutePath == "/api/v1/indexer"
                        ? Json("""[{"id":1,"name":"Books"}]""")
                        : Json("""[{"guid":"p1","title":"Le Guin - The Left Hand of Darkness [EPUB]","size":800000,"protocol":"usenet","downloadUrl":"http://prowlarr.test/1/download","indexer":"NZBs","categories":[{"id":7020,"name":"Books/EBook"}],"publishDate":"2025-01-01T00:00:00Z"}]""");
                case "qbit.test" when url.AbsolutePath == "/api/v2/auth/login":
                {
                    var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                    var good = form.Contains("password=adminpass", StringComparison.Ordinal);
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(good ? "Ok." : "Fails.") };
                    if (good)
                    {
                        response.Headers.Add("Set-Cookie", "SID=q1; HttpOnly; path=/");
                    }

                    return response;
                }

                case "qbit.test" when !request.Headers.TryGetValues("Cookie", out _):
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);
                case "qbit.test" when url.AbsolutePath == "/api/v2/torrents/info":
                    Assert.Equal("shelf-grab-7", query["tag"]);
                    return Json("""[{"hash":"h","progress":1,"state":"stalledUP","content_path":"/downloads/Book","save_path":"/downloads"}]""");
                case "qbit.test" when url.AbsolutePath == "/api/v2/torrents/delete":
                    Removed.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                    return new HttpResponseMessage(HttpStatusCode.OK);
                case "qbit.test":
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") };
                case "sab.test" when query["apikey"] != "sab-key":
                    return Json("""{"status":false,"error":"API Key Incorrect"}""");
                case "sab.test" when query["name"] == "delete":
                    Assert.Equal("1", query["del_files"]);
                    Removed.Add($"{query["mode"]}:{query["value"]}");
                    return Json("""{"status":true}""");
                case "sab.test" when query["mode"] == "addfile":
                    Assert.Equal("shelf-grab-8", query["nzbname"]);
                    return Json("""{"status":true,"nzo_ids":["SABnzbd_nzo_1"]}""");
                case "sab.test" when query["mode"] == "queue":
                    return Json("""{"queue":{"slots":[]}}""");
                case "sab.test" when query["mode"] == "history":
                    return Json("""{"history":{"slots":[{"nzo_id":"SABnzbd_nzo_1","status":"Completed","storage":"/downloads/complete/Book"}]}}""");
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }
    }
}

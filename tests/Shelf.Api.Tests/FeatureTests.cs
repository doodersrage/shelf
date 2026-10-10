using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class FeatureTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.Client;

    [Fact]
    public async Task A_file_is_fingerprinted_once_and_sync_reads_the_fingerprint()
    {
        var book = await CreateAsync(new CreateBookRequest("Fingerprinted", "Someone", BookStatus.Want, null));
        using (var content = new MultipartFormDataContent { { new ByteArrayContent(BooksEndpointTests.SampleEpub("Once only.")), "file", "once.epub" } })
        {
            await _client.PostAsync($"/books/{book.Id}/ebook", content);
        }

        var store = factory.Services.GetRequiredService<EbookStore>();
        string stored;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            stored = Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.IgnoreQueryFilters(db.Books)
                .Single(item => item.Id == book.Id).EbookStoredName!;
        }

        var sidecar = Path.Combine(store.Root, stored + Fingerprint.Extension);
        Assert.True(File.Exists(sidecar));
        var hash = File.ReadAllText(sidecar);
        Assert.Equal(hash, await store.HashAsync(stored, CancellationToken.None));

        // The catalog reports the kept fingerprint without reading the file again.
        const string marker = "0000000000000000000000000000000000000000000000000000000000000000";
        await File.WriteAllTextAsync(sidecar, marker);
        var catalog = await _client.GetFromJsonAsync<SyncCatalog>("/books/sync", JsonOptions);
        Assert.Equal(marker, catalog!.Books.Single(item => item.Title == "Fingerprinted").Ebook!.Sha256);

        await _client.DeleteAsync($"/books/{book.Id}/ebook");
        Assert.False(File.Exists(sidecar));
    }

    [Fact]
    public async Task The_app_has_an_icon_and_a_manifest()
    {
        var page = await _client.GetStringAsync("/");
        Assert.Contains("rel=\"manifest\"", page);
        Assert.Contains("apple-touch-icon", page);

        var manifest = await _client.GetAsync("/manifest.webmanifest");
        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        Assert.Contains("\"standalone\"", await manifest.Content.ReadAsStringAsync());
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/favicon.svg")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/icon-192.png")).StatusCode);
    }

    [Fact]
    public async Task Playback_speed_is_kept_for_each_reader()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(factory.ReaderId);
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        Assert.Equal(100, await BookRules.GetAudioSpeedAsync(db));
        await BookRules.SetAudioSpeedAsync(db, 150);
        Assert.Equal(150, await BookRules.GetAudioSpeedAsync(db));
        await BookRules.SetAudioSpeedAsync(db, 1000);
        Assert.Equal(300, await BookRules.GetAudioSpeedAsync(db));

        var book = await CreateAsync(new CreateBookRequest("Heard Quickly", "Someone", BookStatus.Reading, null));
        using (var content = new MultipartFormDataContent { { new StreamContent(BooksEndpointTests.ZipText("01.mp3", "a track")), "file", "heard.zip" } })
        {
            await _client.PostAsync($"/books/{book.Id}/audio", content);
        }

        var player = await _client.GetStringAsync($"/library/{book.Id}/listen");
        Assert.Contains("Sleep timer", player);
        Assert.Contains("At the end of this track", player);
        Assert.Contains("1.5×", player);
    }

    [Fact]
    public async Task Highlights_gather_with_quotes()
    {
        var book = await CreateAsync(new CreateBookRequest("Gathered Passages", "Someone", BookStatus.Reading, null));
        await _client.PostAsJsonAsync($"/books/{book.Id}/quotes", new CreateQuoteRequest("A quoted line.", 12), JsonOptions);
        await _client.PostAsJsonAsync($"/books/{book.Id}/highlights", new CreateHighlightRequest("A marked passage", 2, "Why it matters."), JsonOptions);

        var page = await _client.GetStringAsync("/quotes");
        Assert.Contains("Quotes &amp; highlights", page);
        Assert.Contains("A quoted line.", page);
        Assert.Contains("A marked passage", page);
        Assert.Contains("Why it matters.", page);
        Assert.Contains("Chapter 3", page);
        Assert.Contains($"/library/{book.Id}/read?chapter=2", page);
    }

    [Fact]
    public async Task A_goodreads_export_comes_in_with_shelves_series_and_dates()
    {
        const string csv = """"""
            Book Id,Title,Author,Author l-f,Additional Authors,ISBN,ISBN13,My Rating,Average Rating,Publisher,Binding,Number of Pages,Year Published,Original Publication Year,Date Read,Date Added,Bookshelves,Bookshelves with positions,Exclusive Shelf,My Review,Spoiler,Private Notes,Read Count,Owned Copies
            13642,"A Wizard of Earthsea (Earthsea Cycle, #1)",Ursula K. Le Guin,"Le Guin, Ursula K.",,"=""0547773749""","=""9780547773742""",5,4.01,Houghton Mifflin,Paperback,183,2012,1968,2024/03/14,2023/12/01,"fantasy, favourites",,read,"Wonderful.<br/>Read it twice.",,A note to self,1,0
            4,"The Dispossessed",Ursula K. Le Guin,"Le Guin, Ursula K.",,"=""""","=""""",0,4.21,Harper,Hardcover,387,1994,1974,,2024/01/02,currently-reading,,currently-reading,,,,0,0
            5,"Kindred",Octavia E. Butler,"Butler, Octavia E.",,"=""""","=""""",0,4.28,Beacon,Paperback,264,2003,1979,,2024/01/03,to-read,,to-read,,,,0,0
            6,"Gravity's Rainbow",Thomas Pynchon,"Pynchon, Thomas",,"=""""","=""""",0,4.0,Penguin,Paperback,776,2006,1973,,2024/01/04,did-not-finish,,did-not-finish,,,,0,0
            """""";
        var response = await PostCsvAsync(csv.Replace("            ", ""), "goodreads_library_export.csv");
        Assert.Contains("csv=done&source=Goodreads&added=4&skipped=0", response.RequestMessage?.RequestUri?.Query);

        var books = await _client.GetFromJsonAsync<BookResponse[]>("/books?author=Ursula%20K.%20Le%20Guin", JsonOptions);
        var wizard = books!.Single(book => book.Title == "A Wizard of Earthsea");
        Assert.Equal("Earthsea Cycle", wizard.Series);
        Assert.Equal(1, wizard.SeriesNumber);
        Assert.Equal(BookStatus.Finished, wizard.Status);
        Assert.Equal(new DateOnly(2024, 3, 14), wizard.FinishedOn);
        Assert.Equal(wizard.FinishedOn, wizard.StartedOn);
        Assert.Equal("9780547773742", wizard.Isbn);
        Assert.Equal(5, wizard.Rating);
        Assert.Equal(1968, wizard.Year);
        Assert.Equal(183, wizard.Pages);
        Assert.Equal(BookFormat.Paperback, wizard.Format);
        Assert.Equal("Wonderful.\nRead it twice.", wizard.Review);
        Assert.Equal("A note to self", wizard.Notes);
        Assert.Equal(["fantasy", "favourites"], wizard.Tags);
        Assert.Equal(new DateTimeOffset(2023, 12, 1, 0, 0, 0, TimeSpan.Zero), wizard.AddedAt);
        Assert.Equal(BookStatus.Reading, books!.Single(book => book.Title == "The Dispossessed").Status);
        var all = await _client.GetFromJsonAsync<BookResponse[]>("/books", JsonOptions);
        Assert.Equal(BookStatus.Want, all!.Single(book => book.Title == "Kindred").Status);
        Assert.Equal(BookStatus.Abandoned, all!.Single(book => book.Title == "Gravity's Rainbow").Status);

        var again = await PostCsvAsync(csv.Replace("            ", ""), "goodreads_library_export.csv");
        Assert.Contains("added=0&skipped=4", again.RequestMessage?.RequestUri?.Query);
    }

    [Fact]
    public async Task A_storygraph_export_comes_in_with_status_and_half_stars()
    {
        const string csv = """"""
            Title,Authors,Contributors,ISBN/UID,Format,Read Status,Date Added,Last Date Read,Dates Read,Read Count,Moods,Pace,Character- or Plot-Driven?,Strong Character Development?,Loveable Characters?,Diverse Characters?,Flawed Characters?,Star Rating,Review,Content Warnings,Content Warning Description,Tags,Owned?
            Piranesi,Susanna Clarke,,9781635575637,hardcover,read,2024/02/10,2024/02/20,2024/02/12-2024/02/20,1,mysterious,medium,Character,,,,,4.5,"A house of tides, endless.",,,"fantasy, mystery",Yes
            The Overstory,Richard Powers,,9780393635522,digital,did-not-finish,2024/03/01,,,0,,,,,,,,,,,,,No
            """""";
        var response = await PostCsvAsync(csv.Replace("            ", ""), "storygraph.csv");
        Assert.Contains("csv=done&source=StoryGraph&added=2", response.RequestMessage?.RequestUri?.Query);
        var books = await _client.GetFromJsonAsync<BookResponse[]>("/books", JsonOptions);
        var piranesi = books!.Single(book => book.Title == "Piranesi");
        Assert.Equal(BookStatus.Finished, piranesi.Status);
        Assert.Equal(5, piranesi.Rating);
        Assert.Equal(BookFormat.Hardcover, piranesi.Format);
        Assert.Equal(new DateOnly(2024, 2, 20), piranesi.FinishedOn);
        Assert.Equal("A house of tides, endless.", piranesi.Review);
        Assert.Equal(["fantasy", "mystery"], piranesi.Tags);
        var overstory = books!.Single(book => book.Title == "The Overstory");
        Assert.Equal(BookStatus.Abandoned, overstory.Status);
        Assert.Equal(BookFormat.Ebook, overstory.Format);

        var junk = await PostCsvAsync("a,b,c\n1,2,3\n", "other.csv");
        Assert.Contains("csv=unreadable", junk.RequestMessage?.RequestUri?.Query);
    }

    [Fact]
    public async Task Many_books_change_at_once_and_only_the_readers_own()
    {
        var first = await CreateAsync(new CreateBookRequest("Bulk One", "Someone", BookStatus.Want, null));
        var second = await CreateAsync(new CreateBookRequest("Bulk Two", "Someone", BookStatus.Want, null, Tags: ["old"]));
        var other = await factory.SignUpAsync("Bulk Neighbour");
        var theirs = await (await other.PostAsJsonAsync("/books", new CreateBookRequest("Not Yours", "Someone", BookStatus.Want, null), JsonOptions))
            .Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        int[] ids = [first.Id, second.Id, theirs!.Id];

        async Task<int> BulkAsync(BulkRequest request) =>
            (await (await _client.PostAsJsonAsync("/books/bulk", request, JsonOptions)).Content.ReadFromJsonAsync<BulkResult>(JsonOptions))!.Changed;

        Assert.Equal(2, await BulkAsync(new BulkRequest(ids, Status: BookStatus.Finished)));
        Assert.Equal(2, await BulkAsync(new BulkRequest(ids, AddTag: "  Summer Reads ")));
        Assert.Equal(2, await BulkAsync(new BulkRequest(ids, RemoveTag: "old")));
        Assert.Equal(2, await BulkAsync(new BulkRequest(ids, Loved: true)));

        var one = await _client.GetFromJsonAsync<BookResponse>($"/books/{first.Id}", JsonOptions);
        var two = await _client.GetFromJsonAsync<BookResponse>($"/books/{second.Id}", JsonOptions);
        Assert.Equal(BookStatus.Finished, one!.Status);
        Assert.NotNull(one.FinishedOn);
        Assert.True(one.Loved);
        Assert.Equal(["summer reads"], one.Tags);
        Assert.Equal(["summer reads"], two!.Tags);
        var untouched = await other.GetFromJsonAsync<BookResponse>($"/books/{theirs.Id}", JsonOptions);
        Assert.Equal(BookStatus.Want, untouched!.Status);
        Assert.Empty(untouched.Tags);

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/books/bulk", new BulkRequest([]), JsonOptions)).StatusCode);
        Assert.Equal(2, await BulkAsync(new BulkRequest(ids, Delete: true)));
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/books/{first.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync($"/books/{theirs.Id}")).StatusCode);
        Assert.Contains("Choose all shown", await _client.GetStringAsync("/?view=list"));
    }

    [Fact]
    public async Task Words_inside_a_book_can_be_found_by_who_may_open_it()
    {
        var book = await CreateAsync(new CreateBookRequest("Searchable Waters", "Someone", BookStatus.Reading, null));
        using (var content = new MultipartFormDataContent { { new ByteArrayContent(BooksEndpointTests.SampleEpub("The grey heron waits by the cold river.")), "file", "waters.epub" } })
        {
            await _client.PostAsync($"/books/{book.Id}/ebook", content);
        }

        SearchHit[]? hits = null;
        for (var attempt = 0; attempt < 60 && (hits is null || hits.Length == 0); attempt++)
        {
            hits = await _client.GetFromJsonAsync<SearchHit[]>("/books/search?q=HERON%20waits", JsonOptions);
            if (hits!.Length == 0)
            {
                await Task.Delay(250);
            }
        }

        var hit = Assert.Single(hits!);
        Assert.Equal(book.Id, hit.BookId);
        Assert.Equal("heron waits", hit.Match);
        Assert.Equal("Chapter 1", hit.Where);
        Assert.EndsWith("by the cold river.", hit.After);
        Assert.Equal($"/library/{book.Id}/read?chapter=0", hit.Open);
        Assert.Empty(await _client.GetFromJsonAsync<SearchHit[]>("/books/search?q=%25", JsonOptions) ?? []);

        var page = await _client.GetStringAsync("/search?q=cold%20river");
        Assert.Contains("<mark>cold river</mark>", page);
        Assert.Contains("Searchable Waters", page);

        var stranger = await factory.SignUpAsync("Search Stranger");
        Assert.Empty(await stranger.GetFromJsonAsync<SearchHit[]>("/books/search?q=heron", JsonOptions) ?? []);
        var strangerId = await factory.ReaderIdAsync("Search Stranger");
        await _client.PostAsJsonAsync($"/books/{book.Id}/lend", new LendRequest(strangerId), JsonOptions);
        Assert.Single(await stranger.GetFromJsonAsync<SearchHit[]>("/books/search?q=heron", JsonOptions) ?? []);
    }

    [Fact]
    public async Task An_e_reader_browses_the_opds_catalog_with_the_device_key()
    {
        var book = await CreateAsync(new CreateBookRequest("Catalogued <Tales>", "Someone & Co", BookStatus.Reading, null, CoverUrl: "https://example.org/cover.jpg"));
        await CreateAsync(new CreateBookRequest("No File Here", "Someone", BookStatus.Reading, null));
        var epub = BooksEndpointTests.SampleEpub("For the e-reader.");
        using (var content = new MultipartFormDataContent { { new ByteArrayContent(epub), "file", "tales.epub" } })
        {
            await _client.PostAsync($"/books/{book.Id}/ebook", content);
        }

        string key;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            key = await ReaderRules.NewKeyAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), factory.ReaderId);
        }

        var anonymous = factory.CreateClient();
        var refused = await anonymous.GetAsync("/opds");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Contains("Basic", refused.Headers.WwwAuthenticate.ToString());

        var wrong = factory.CreateClient();
        wrong.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String("kobo:not-the-key"u8.ToArray()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/opds")).StatusCode);

        var reader = factory.CreateClient();
        reader.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"kobo:{key}")));
        var root = await reader.GetAsync("/opds");
        Assert.Equal("application/atom+xml", root.Content.Headers.ContentType?.MediaType);
        var rootXml = System.Xml.Linq.XDocument.Parse(await root.Content.ReadAsStringAsync());
        Assert.Contains(rootXml.Descendants().Where(element => element.Name.LocalName == "link"), link => (string?)link.Attribute("href") == "/opds/books?status=Reading");

        var reading = System.Xml.Linq.XDocument.Parse(await reader.GetStringAsync("/opds/books?status=Reading"));
        var entries = reading.Descendants().Where(element => element.Name.LocalName == "entry").ToList();
        var entry = entries.Single(item => item.Elements().Single(element => element.Name.LocalName == "title").Value == "Catalogued <Tales>");
        Assert.DoesNotContain(entries, item => item.Value.Contains("No File Here"));
        var acquisition = entry.Elements().Single(element => element.Name.LocalName == "link" && (string?)element.Attribute("rel") == "http://opds-spec.org/acquisition");
        Assert.Equal("application/epub+zip", (string?)acquisition.Attribute("type"));
        Assert.Contains(entry.Elements(), element => element.Name.LocalName == "link" && (string?)element.Attribute("href") == "https://example.org/cover.jpg");

        var file = await reader.GetAsync((string)acquisition.Attribute("href")!);
        Assert.Equal(epub, await file.Content.ReadAsByteArrayAsync());
        Assert.Equal("tales.epub", file.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        // The key reaches the catalog and sync, never the rest of the API.
        Assert.Equal(HttpStatusCode.Unauthorized, (await reader.GetAsync("/books")).StatusCode);
    }

    [Fact]
    public async Task A_forgotten_password_is_reset_from_an_emailed_link()
    {
        var reader = await factory.SignUpAsync("Forgetful Reader");
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PutAsJsonAsync("/account/email", new EmailSettingsRequest("not an address", false), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await reader.PutAsJsonAsync("/account/email", new EmailSettingsRequest("forgetful@example.org", true), JsonOptions)).StatusCode);

        var visitor = factory.CreateClient(new() { AllowAutoRedirect = false });
        var before = factory.Mail.Sent.Count;
        var unknown = await ShelfApiFactory.PostFormAsync(visitor, "/forgot", "/account/forgot", new() { ["who"] = "Nobody Here" });
        Assert.Equal("/forgot?sent=1", unknown.Headers.Location?.OriginalString);
        Assert.Equal(before, factory.Mail.Sent.Count);

        var asked = await ShelfApiFactory.PostFormAsync(visitor, "/forgot", "/account/forgot", new() { ["who"] = "FORGETFUL@example.org" });
        Assert.Equal("/forgot?sent=1", asked.Headers.Location?.OriginalString);
        var message = factory.Mail.Sent.Last();
        Assert.Equal("forgetful@example.org", message.To);
        var link = System.Text.RegularExpressions.Regex.Match(message.Body, @"/reset\?token=([A-Za-z0-9_-]+)");
        Assert.True(link.Success);
        Assert.Contains("Choose a new password", await visitor.GetStringAsync(link.Value));

        var mismatch = await ShelfApiFactory.PostFormAsync(visitor, link.Value, "/account/reset", new()
        {
            ["token"] = link.Groups[1].Value,
            ["password"] = "a brand new password",
            ["confirm"] = "something different",
        });
        Assert.Contains("problem=PasswordsDiffer", mismatch.Headers.Location?.OriginalString);

        var reset = await ShelfApiFactory.PostFormAsync(visitor, link.Value, "/account/reset", new()
        {
            ["token"] = link.Groups[1].Value,
            ["password"] = "a brand new password",
            ["confirm"] = "a brand new password",
        });
        Assert.Equal("/signin?notice=reset", reset.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await reader.GetAsync("/books")).StatusCode);
        Assert.Contains("has expired or was used already", await visitor.GetStringAsync(link.Value));

        var signedIn = await ShelfApiFactory.PostFormAsync(visitor, "/signin", "/account/signin", new()
        {
            ["name"] = "Forgetful Reader",
            ["password"] = "a brand new password",
        });
        Assert.Equal("/", signedIn.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task A_daily_email_goes_out_when_a_loan_needs_attention()
    {
        var borrower = await factory.SignUpAsync("Reminded Reader");
        var borrowerId = await factory.ReaderIdAsync("Reminded Reader");
        await borrower.PutAsJsonAsync("/account/email", new EmailSettingsRequest("reminded@example.org", true), JsonOptions);
        var book = await CreateAsync(new CreateBookRequest("Long Overdue Book", "Someone", BookStatus.Want, null));
        await _client.PostAsJsonAsync($"/books/{book.Id}/lend", new LendRequest(borrowerId, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3)), JsonOptions);

        var mailer = factory.Services.GetRequiredService<ReminderMailer>();
        await mailer.SendDueAsync(CancellationToken.None);
        var reminder = factory.Mail.Sent.Last(item => item.To == "reminded@example.org");
        Assert.Contains("1 book you borrowed is overdue", reminder.Body);
        var count = factory.Mail.Sent.Count(item => item.To == "reminded@example.org");

        await mailer.SendDueAsync(CancellationToken.None);
        Assert.Equal(count, factory.Mail.Sent.Count(item => item.To == "reminded@example.org"));
    }

    [Fact]
    public async Task The_running_version_is_shown_and_served()
    {
        var anonymous = factory.CreateClient();
        var version = await anonymous.GetFromJsonAsync<VersionResponse>("/version", JsonOptions);
        Assert.Matches(@"^\d+\.\d+\.\d+", version!.Version);
        Assert.Equal(typeof(Program).Assembly.GetName().Version!.ToString(3), version.Version.Split('-')[0]);
        Assert.Contains($"Shelf {version.Version}", await _client.GetStringAsync("/"));
    }

    [Fact]
    public async Task Health_checks_answer_without_signing_in()
    {
        var anonymous = factory.CreateClient();
        var health = await anonymous.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("Healthy", await health.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", await anonymous.GetStringAsync("/alive"));
    }

    [Fact]
    public async Task Nightly_backups_keep_the_last_few_and_an_admin_can_take_one()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"shelf-backups-{Guid.NewGuid():N}");
        try
        {
            var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Backup:Folder"] = folder, ["Backup:Keep"] = "2" })
                .Build();
            var schedule = new BackupSchedule(
                factory.Services.GetRequiredService<IServiceScopeFactory>(),
                configuration,
                factory.Services.GetRequiredService<EbookStore>(),
                factory.Services.GetRequiredService<AudioStore>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<BackupSchedule>.Instance);
            for (var round = 0; round < 3; round++)
            {
                await schedule.TakeAsync(CancellationToken.None);
                await Task.Delay(5);
            }

            var kept = schedule.List();
            Assert.Equal(2, kept.Count);
            using (var zip = System.IO.Compression.ZipFile.OpenRead(Path.Combine(folder, kept[0].Name)))
            {
                Assert.NotNull(zip.GetEntry("shelf.db"));
                Assert.DoesNotContain(zip.Entries, entry => entry.FullName.StartsWith("ebooks/", StringComparison.Ordinal));
            }

            Assert.Null(schedule.PathOf("../shelf.db"));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        var taken = await _client.PostAsync("/admin/backups", null);
        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
        var backup = await taken.Content.ReadFromJsonAsync<AutomaticBackup>(JsonOptions);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/admin/backups/{backup!.Name}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/admin/backups/not-a-backup.zip")).StatusCode);
        Assert.Contains("Nightly backups", await _client.GetStringAsync("/admin"));
        var reader = await factory.SignUpAsync("Backup Bystander");
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"/admin/backups/{backup.Name}")).StatusCode);
    }

    private async Task<HttpResponseMessage> PostCsvAsync(string csv, string name)
    {
        using var content = new MultipartFormDataContent { { new StringContent(csv), "file", name } };
        return await _client.PostAsync("/books/import/csv", content);
    }

    private async Task<BookResponse> CreateAsync(CreateBookRequest request)
    {
        var response = await _client.PostAsJsonAsync("/books", request, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
    }
}

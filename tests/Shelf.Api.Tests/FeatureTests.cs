using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
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
    public async Task Emails_are_in_the_language_the_reader_chose()
    {
        var reader = await factory.SignUpAsync("Lectora");
        var readerId = await factory.ReaderIdAsync("Lectora");
        await reader.PutAsJsonAsync("/account/email", new EmailSettingsRequest("lectora@example.org", true), JsonOptions);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            db.Readers.Single(item => item.Id == readerId).Language = "es";
            await db.SaveChangesAsync();
        }

        var book = await CreateAsync(new CreateBookRequest("Libro Atrasado", "Alguien", BookStatus.Want, null));
        await _client.PostAsJsonAsync($"/books/{book.Id}/lend", new LendRequest(readerId, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3)), JsonOptions);
        await factory.Services.GetRequiredService<ReminderMailer>().SendDueAsync(CancellationToken.None);
        var reminder = factory.Mail.Sent.Last(item => item.To == "lectora@example.org");
        Assert.Equal("Tus préstamos en Shelf", reminder.Subject);
        Assert.StartsWith("Hola, Lectora:", reminder.Body);
        Assert.Contains("1 libro que tomaste prestado está atrasado.", reminder.Body);

        // Asked for from an English browser, the reset still comes in Spanish; the work after it is in English again.
        var visitor = factory.CreateClient(new() { AllowAutoRedirect = false });
        visitor.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
        await ShelfApiFactory.PostFormAsync(visitor, "/forgot", "/account/forgot", new() { ["who"] = "Lectora" });
        var reset = factory.Mail.Sent.Last(item => item.To == "lectora@example.org");
        Assert.Equal("Restablece tu contraseña de Shelf", reset.Subject);
        Assert.Matches(@"/reset\?token=[A-Za-z0-9_-]+", reset.Body);
        Assert.Contains("a link to choose a new password is on its way", await visitor.GetStringAsync("/forgot?sent=1"));
    }

    [Fact]
    public async Task The_running_version_is_shown_and_served()
    {
        var anonymous = factory.CreateClient();
        var version = await anonymous.GetFromJsonAsync<VersionResponse>("/version", JsonOptions);
        Assert.Matches(@"^\d+\.\d+\.\d+", version!.Version);
        Assert.Equal(typeof(Program).Assembly.GetName().Version!.ToString(3), version.Version.Split('-')[0]);
        var page = await _client.GetStringAsync("/");
        Assert.Contains($"Shelf {version.Version}", page);
        // Under the AGPL every page offers the source; a changed copy points the link at its own.
        Assert.Contains($"<a href=\"{ShelfVersion.Source}\">Source code</a>", page);
        static IConfiguration With(string? url) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["SourceUrl"] = url }).Build();
        Assert.Equal("https://git.example.org/me/shelf", ShelfVersion.SourceUrl(With("https://git.example.org/me/shelf")));
        Assert.Equal(ShelfVersion.Source, ShelfVersion.SourceUrl(With("javascript:alert(1)")));
        Assert.Equal(ShelfVersion.Source, ShelfVersion.SourceUrl(With(null)));
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
                factory.Services.GetRequiredService<CoverStore>(),
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

    [Fact]
    public async Task Search_ranks_places_and_overlooks_accents_and_partial_words()
    {
        async Task<BookResponse> WithTextAsync(string title, string sentence)
        {
            var book = await CreateAsync(new CreateBookRequest(title, "Someone", BookStatus.Reading, null));
            using var content = new MultipartFormDataContent { { new ByteArrayContent(BooksEndpointTests.SampleEpub(sentence)), "file", "text.epub" } };
            await _client.PostAsync($"/books/{book.Id}/ebook", content);
            return book;
        }

        var once = await WithTextAsync("Mentions Marmalade Once", "There was marmalade on the table, and tea.");
        var often = await WithTextAsync("Marmalade Everywhere", "Marmalade for breakfast. Marmalade for lunch. Marmalade, marmalade, marmalade.");
        var accented = await WithTextAsync("The Corner Café", "They met at the little café by the harbour.");

        // Text is indexed in the background, so wait until as many books as expected have been read.
        async Task<SearchHit[]> FindAsync(string query, int books = 1)
        {
            SearchHit[] hits = [];
            for (var attempt = 0; attempt < 60; attempt++)
            {
                hits = await _client.GetFromJsonAsync<SearchHit[]>($"/books/search?q={Uri.EscapeDataString(query)}", JsonOptions) ?? [];
                if (hits.Select(hit => hit.BookId).Distinct().Count() >= books)
                {
                    break;
                }

                await Task.Delay(250);
            }

            return hits;
        }

        var marmalade = await FindAsync("marmalade", books: 2);
        Assert.Equal([often.Id, once.Id], marmalade.Select(hit => hit.BookId).Distinct());

        var cafe = Assert.Single(await FindAsync("cafe by the"));
        Assert.Equal(accented.Id, cafe.BookId);
        Assert.Equal("café by the", cafe.Match);

        Assert.Contains(await FindAsync("harbo"), hit => hit.BookId == accented.Id);
        Assert.Empty(await _client.GetFromJsonAsync<SearchHit[]>("/books/search?q=%22%22%22", JsonOptions) ?? []);
    }

    [Fact]
    public async Task A_large_shelf_comes_a_page_at_a_time()
    {
        var reader = await factory.SignUpAsync("Prolific Reader");
        for (var number = 1; number <= 65; number++)
        {
            await reader.PostAsJsonAsync("/books", new CreateBookRequest($"Volume {number:000}", "Someone", BookStatus.Want, null), JsonOptions);
        }

        var page = await reader.GetStringAsync("/");
        Assert.Contains("65 books, showing 60", page);
        Assert.Contains("Show 5 more", page);
        Assert.Contains("Volume 060", page);
        Assert.DoesNotContain("Volume 061", page);

        var first = await reader.GetAsync("/books?sort=title&skip=0&take=20");
        Assert.Equal("65", first.Headers.GetValues("X-Total-Count").Single());
        var titles = (await first.Content.ReadFromJsonAsync<BookResponse[]>(JsonOptions))!.Select(book => book.Title).ToList();
        Assert.Equal(20, titles.Count);
        Assert.Equal("Volume 001", titles[0]);
        var last = await reader.GetFromJsonAsync<BookResponse[]>("/books?sort=title&skip=60&take=20", JsonOptions);
        Assert.Equal(5, last!.Length);
        Assert.Equal(65, (await reader.GetFromJsonAsync<BookResponse[]>("/books", JsonOptions))!.Length);
    }

    [Fact]
    public void Authenticator_codes_follow_rfc_6238()
    {
        // The RFC's own example: the ASCII secret "12345678901234567890" at 59 seconds gives 94287082, of which 287082.
        const string secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
        Assert.Equal("287082", TwoFactor.Code(secret, 1));
        Assert.True(TwoFactor.Verify(secret, "287 082", DateTimeOffset.FromUnixTimeSeconds(59)));
        Assert.True(TwoFactor.Verify(secret, "287082", DateTimeOffset.FromUnixTimeSeconds(89)));
        Assert.False(TwoFactor.Verify(secret, "287082", DateTimeOffset.FromUnixTimeSeconds(200)));
        Assert.False(TwoFactor.Verify(secret, "28708", DateTimeOffset.FromUnixTimeSeconds(59)));
        Assert.StartsWith("otpauth://totp/Shelf%3ATenar?secret=", TwoFactor.Uri(secret, "Tenar"));
    }

    [Fact]
    public async Task Two_step_sign_in_asks_for_a_code_and_each_device_can_be_signed_out()
    {
        var phone = await factory.SignUpAsync("Careful Reader");
        var start = await (await phone.PostAsync("/account/two-factor/start", null)).Content.ReadFromJsonAsync<TwoFactorStart>(JsonOptions);
        string Now() => TwoFactor.Code(start!.Secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PostAsJsonAsync("/account/two-factor/confirm", new TwoFactorCodeRequest("000000"), JsonOptions)).StatusCode);
        var confirmed = await phone.PostAsJsonAsync("/account/two-factor/confirm", new TwoFactorCodeRequest(Now()), JsonOptions);
        var recovery = (await confirmed.Content.ReadFromJsonAsync<RecoveryCodes>(JsonOptions))!.Codes;
        Assert.Equal(10, recovery.Length);

        var laptop = factory.CreateClient(new() { AllowAutoRedirect = false });
        var password = await ShelfApiFactory.PostFormAsync(laptop, "/signin", "/account/signin", new() { ["name"] = "Careful Reader", ["password"] = ShelfApiFactory.Password });
        Assert.Equal("/signin/code", password.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.GetAsync("/books")).StatusCode);
        var wrong = await ShelfApiFactory.PostFormAsync(laptop, "/signin/code", "/account/signin/code", new() { ["code"] = "123456" });
        Assert.Contains("problem=CodeWrong", wrong.Headers.Location?.OriginalString);
        var right = await ShelfApiFactory.PostFormAsync(laptop, "/signin/code", "/account/signin/code", new() { ["code"] = Now() });
        Assert.Equal("/", right.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/books")).StatusCode);

        // A recovery code stands in for the authenticator, once.
        var tablet = factory.CreateClient(new() { AllowAutoRedirect = false });
        await ShelfApiFactory.PostFormAsync(tablet, "/signin", "/account/signin", new() { ["name"] = "Careful Reader", ["password"] = ShelfApiFactory.Password });
        Assert.Equal("/", (await ShelfApiFactory.PostFormAsync(tablet, "/signin/code", "/account/signin/code", new() { ["code"] = recovery[0] })).Headers.Location?.OriginalString);
        var spare = factory.CreateClient(new() { AllowAutoRedirect = false });
        await ShelfApiFactory.PostFormAsync(spare, "/signin", "/account/signin", new() { ["name"] = "Careful Reader", ["password"] = ShelfApiFactory.Password });
        Assert.Contains("problem=CodeWrong", (await ShelfApiFactory.PostFormAsync(spare, "/signin/code", "/account/signin/code", new() { ["code"] = recovery[0] })).Headers.Location?.OriginalString);

        var sessions = await phone.GetFromJsonAsync<SessionSummary[]>("/account/sessions", JsonOptions);
        Assert.Equal(3, sessions!.Length);
        Assert.Single(sessions, session => session.Current);
        Assert.Contains("Signed-in devices", await phone.GetStringAsync("/account"));
        foreach (var other in sessions.Where(session => !session.Current))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await phone.DeleteAsync($"/account/sessions/{other.Id}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.GetAsync("/books")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/books")).StatusCode);

        // An admin's new password is the way back from a lost phone: two-step sign-in turns off.
        var id = await factory.ReaderIdAsync("Careful Reader");
        var reset = await (await _client.PostAsync($"/admin/readers/{id}/password", null)).Content.ReadFromJsonAsync<TemporaryPassword>(JsonOptions);
        var after = factory.CreateClient(new() { AllowAutoRedirect = false });
        var back = await ShelfApiFactory.PostFormAsync(after, "/signin", "/account/signin", new() { ["name"] = "Careful Reader", ["password"] = reset!.Password });
        Assert.Equal("/", back.Headers.Location?.OriginalString);
    }

    [Fact]
    public void Dates_follow_the_readers_region()
    {
        var date = new DateOnly(2024, 3, 14);
        string In(string culture, Func<string> write)
        {
            var before = System.Globalization.CultureInfo.CurrentCulture;
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
            try
            {
                return write();
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = before;
            }
        }

        Assert.Equal("Mar 14, 2024", In("en-US", () => date.Medium()));
        Assert.Equal("14 Mar 2024", In("en-GB", () => date.Medium()));
        Assert.Equal("14. März 2024", In("de-DE", () => date.Medium()));
        Assert.Equal("2024 3月 14", In("ja-JP", () => date.Medium()));
        Assert.Equal("March 2024", In("en-US", () => date.MonthYear()));
        Assert.Equal("14 Mar", In("en-GB", () => date.MonthDay()));
    }

    [Fact]
    public async Task A_page_writes_dates_for_the_browser_or_the_readers_choice()
    {
        var reader = await factory.SignUpAsync("Regional Reader");
        await reader.PostAsJsonAsync("/books", new CreateBookRequest("Dated Book", "Someone", BookStatus.Finished, null, StartedOn: new DateOnly(2024, 3, 1), FinishedOn: new DateOnly(2024, 3, 14)), JsonOptions);

        async Task<string> ListAsync(string? language)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/?view=list");
            if (language is not null)
            {
                request.Headers.AcceptLanguage.ParseAdd(language);
            }

            // The page encodes letters such as ä as character references; read it as a browser would.
            return WebUtility.HtmlDecode(await (await reader.SendAsync(request)).Content.ReadAsStringAsync());
        }

        Assert.Contains("Finished Mar 14, 2024", await ListAsync(null));
        // A German browser gets the page in German, with German dates.
        Assert.Contains("Gelesen am 14. März 2024", await ListAsync("de-DE"));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var id = await factory.ReaderIdAsync("Regional Reader");
            var me = db.Readers.Single(item => item.Id == id);
            me.Culture = "en-GB";
            await db.SaveChangesAsync();
        }

        // The reader's own choice of region wins over the browser's; the language still follows the browser.
        Assert.Contains("Gelesen am 14 Mar 2024", await ListAsync("de-DE"));
        Assert.Contains("Language and region", await reader.GetStringAsync("/account"));
    }

    [Fact]
    public async Task Pages_are_in_the_browsers_language_or_the_readers_choice()
    {
        var reader = await factory.SignUpAsync("Polyglot Reader");
        async Task<string> PageAsync(string? language)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/authors");
            if (language is not null)
            {
                request.Headers.AcceptLanguage.ParseAdd(language);
            }

            return WebUtility.HtmlDecode(await (await reader.SendAsync(request)).Content.ReadAsStringAsync());
        }

        var english = await PageAsync(null);
        Assert.Contains("<html lang=\"en\">", english);
        Assert.Contains("No authors yet", english);
        var spanish = await PageAsync("es-MX,es;q=0.9");
        Assert.Contains("<html lang=\"es\">", spanish);
        Assert.Contains("Aún no hay autores", spanish);
        Assert.Contains("Pas encore d'auteurs", await PageAsync("fr-FR"));
        Assert.Contains("Noch keine Autoren", await PageAsync("de"));
        Assert.Contains("No authors yet", await PageAsync("ja-JP"));

        // The reader's own choice wins over the browser's.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var id = await factory.ReaderIdAsync("Polyglot Reader");
            db.Readers.Single(item => item.Id == id).Language = "de";
            await db.SaveChangesAsync();
        }

        Assert.Contains("Noch keine Autoren", await PageAsync("es-ES"));

        // Before signing in, the browser's language decides.
        using var signIn = new HttpRequestMessage(HttpMethod.Get, "/signin");
        signIn.Headers.AcceptLanguage.ParseAdd("fr-CA");
        Assert.Contains("Se connecter", WebUtility.HtmlDecode(await (await factory.CreateClient().SendAsync(signIn)).Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task The_same_file_on_a_second_book_waits_until_the_reader_keeps_both()
    {
        var first = await CreateAsync(new CreateBookRequest("Twice Bought", "Someone", BookStatus.Reading, null));
        var second = await CreateAsync(new CreateBookRequest("Twice Bought (Again)", "Someone", BookStatus.Reading, null));
        var epub = BooksEndpointTests.SampleEpub("A sentence found in exactly one file.");
        Assert.Equal("?ebook=saved", (await UploadEbookAsync(_client, first.Id, epub)).RequestMessage!.RequestUri!.Query);

        // The same bytes under another name: held, and the book page asks.
        var held = await UploadEbookAsync(_client, second.Id, epub, "renamed.epub");
        var query = System.Web.HttpUtility.ParseQueryString(held.RequestMessage!.RequestUri!.Query);
        Assert.Equal("duplicate", query["ebook"]);
        var page = WebUtility.HtmlDecode(await held.Content.ReadAsStringAsync());
        Assert.Contains("is already on", page);
        Assert.Contains($"href=\"/library/{first.Id}\"", page);
        Assert.Contains("Keep both", page);
        Assert.Null((await GetBookAsync(second.Id)).EbookFileName);

        var protection = factory.Services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>();
        var store = factory.Services.GetRequiredService<EbookStore>();
        var token = query["held"]!;
        int readerId;
        using (var scope = factory.Services.CreateScope())
        {
            readerId = scope.ServiceProvider.GetRequiredService<ShelfDb>().Books.IgnoreQueryFilters().Single(book => book.Id == first.Id).OwnerId!.Value;
        }

        var upload = Duplicates.Read(protection, token, readerId, second.Id)!;
        Assert.Equal(first.Id, upload.SameAsId);
        Assert.Equal("renamed.epub", upload.FileName);
        Assert.True(File.Exists(Path.Combine(store.HeldRoot, upload.StoredName)));
        Assert.Null(store.OpenPath(upload.StoredName));
        Assert.Null(Duplicates.Read(protection, token, readerId, first.Id));
        Assert.Null(Duplicates.Read(protection, token, readerId + 1000, second.Id));

        // Releasing it puts it back where books keep their files.
        Assert.True(store.Release(upload.StoredName));
        Assert.NotNull(store.OpenPath(upload.StoredName));
        store.Delete(upload.StoredName);

        // The same book taking its own file again is no question; asked to keep both up front, it goes straight on.
        Assert.Equal("?ebook=saved", (await UploadEbookAsync(_client, first.Id, epub)).RequestMessage!.RequestUri!.Query);
        Assert.Equal("?ebook=saved", (await UploadEbookAsync(_client, second.Id, epub, keepBoth: true)).RequestMessage!.RequestUri!.Query);

        // Another reader's shelf is theirs: the same file there is never reported.
        var stranger = await factory.SignUpAsync("Twice Stranger");
        var theirs = await (await stranger.PostAsJsonAsync("/books", new CreateBookRequest("Twice Bought", "Someone", BookStatus.Reading, null), JsonOptions))
            .Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        Assert.Equal("?ebook=saved", (await UploadEbookAsync(stranger, theirs!.Id, epub)).RequestMessage!.RequestUri!.Query);
    }

    [Fact]
    public async Task The_same_recording_on_a_second_book_is_held_and_an_old_hold_is_swept()
    {
        var first = await CreateAsync(new CreateBookRequest("Heard Twice", "Someone", BookStatus.Reading, null));
        var second = await CreateAsync(new CreateBookRequest("Heard Twice Again", "Someone", BookStatus.Reading, null));
        async Task<HttpResponseMessage> UploadAsync(int id)
        {
            using var content = new MultipartFormDataContent { { new StreamContent(BooksEndpointTests.ZipText("01.mp3", "the very same track")), "file", "heard.zip" } };
            return await _client.PostAsync($"/books/{id}/audio", content);
        }

        Assert.Equal("?audio=saved", (await UploadAsync(first.Id)).RequestMessage!.RequestUri!.Query);
        var held = await UploadAsync(second.Id);
        Assert.StartsWith("?audio=duplicate&held=", held.RequestMessage!.RequestUri!.Query);
        Assert.Null((await GetBookAsync(second.Id)).AudioFileName);

        var audio = factory.Services.GetRequiredService<AudioStore>();
        var ebooks = factory.Services.GetRequiredService<EbookStore>();
        var waiting = Directory.GetDirectories(audio.HeldRoot);
        Assert.NotEmpty(waiting);
        Assert.Equal(0, Duplicates.Sweep(ebooks, audio, DateTimeOffset.UtcNow));
        Assert.True(Duplicates.Sweep(ebooks, audio, DateTimeOffset.UtcNow.AddDays(2)) >= waiting.Length);
        Assert.Empty(Directory.GetDirectories(audio.HeldRoot));
        Assert.Equal("heard.zip", (await GetBookAsync(first.Id)).AudioFileName);
    }

    private async Task<BookResponse> GetBookAsync(int id) =>
        (await _client.GetFromJsonAsync<BookResponse>($"/books/{id}", JsonOptions))!;

    private static async Task<HttpResponseMessage> UploadEbookAsync(HttpClient client, int id, byte[] epub, string name = "book.epub", bool keepBoth = false)
    {
        using var content = new MultipartFormDataContent { { new ByteArrayContent(epub), "file", name } };
        if (keepBoth)
        {
            content.Add(new StringContent("true"), "keepBoth");
        }

        return await client.PostAsync($"/books/{id}/ebook", content);
    }

    [Fact]
    public async Task A_book_on_an_open_shelf_is_offered_to_borrow_before_uploading_your_own()
    {
        var lender = await factory.SignUpAsync("Open Lender");
        var theirs = await (await lender.PostAsJsonAsync("/books", new CreateBookRequest("The Tombs of Atuan", "Ursula K. Le Guin", BookStatus.Finished, null), JsonOptions))
            .Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        await UploadEbookAsync(lender, theirs!.Id, BooksEndpointTests.SampleEpub("Arha walks the labyrinth."));
        var mine = await CreateAsync(new CreateBookRequest("the tombs of atuan", "Ursula K. Le Guin", BookStatus.Want, null));

        // A closed shelf is never mentioned.
        Assert.DoesNotContain("Before uploading", await _client.GetStringAsync($"/library/{mine.Id}"));

        await lender.PutAsJsonAsync("/books/shelves/open", new ShelfOpenChange(true), JsonOptions);
        var page = WebUtility.HtmlDecode(await _client.GetStringAsync($"/library/{mine.Id}"));
        Assert.Contains("Before uploading", page);
        Assert.Contains("Open Lender's open shelf", page);
        Assert.Contains("with an e-book", page);
        Assert.Contains("Ask Open Lender to borrow", page);

        // Once this copy has its own e-book, there is nothing to offer.
        await UploadEbookAsync(_client, mine.Id, BooksEndpointTests.SampleEpub("My own copy of the labyrinth."));
        Assert.DoesNotContain("Before uploading", await _client.GetStringAsync($"/library/{mine.Id}"));

        // Matching is by title and author, or ISBN; the lender never sees their own book offered back.
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(factory.ReaderId);
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var all = await Asking.OpenCopiesAsync(db);
        Assert.Single(Asking.Matching(all, null, "The Tombs Of Atuan", "ursula k. le guin"));
        Assert.Empty(Asking.Matching(all, null, "The Tombs of Atuan", "Someone Else"));

        await lender.PutAsJsonAsync("/books/shelves/open", new ShelfOpenChange(false), JsonOptions);
        Assert.Empty(Asking.Matching(await Asking.OpenCopiesAsync(db), null, "The Tombs of Atuan", "Ursula K. Le Guin"));
    }

    [Fact]
    public async Task A_place_read_elsewhere_only_moves_forward()
    {
        var book = await CreateAsync(new CreateBookRequest("Read On A Train", "Someone", BookStatus.Reading, null));
        Assert.Null((await _client.GetFromJsonAsync<PlaceResponse>($"/books/{book.Id}/place", JsonOptions))!.EbookChapter);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PutAsJsonAsync($"/books/{book.Id}/place", new PlaceRequest(3), JsonOptions)).StatusCode);
        await _client.PutAsJsonAsync($"/books/{book.Id}/place", new PlaceRequest(1), JsonOptions);
        Assert.Equal(3, (await _client.GetFromJsonAsync<PlaceResponse>($"/books/{book.Id}/place", JsonOptions))!.EbookChapter);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PutAsJsonAsync($"/books/{book.Id}/place", new PlaceRequest(-1), JsonOptions)).StatusCode);
        var stranger = await factory.SignUpAsync("Place Stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/books/{book.Id}/place")).StatusCode);
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

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        var sidecar = Directory.GetFiles(store.Root, "*" + Fingerprint.Extension)
            .Single(path => File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddMinutes(-1) && File.ReadAllText(path).Length == 64
                && Path.GetFileName(path).StartsWith(Path.GetFileName(path).Split('.')[0], StringComparison.Ordinal)
                && File.Exists(path[..^Fingerprint.Extension.Length]));
        var stored = Path.GetFileName(sidecar[..^Fingerprint.Extension.Length]);
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

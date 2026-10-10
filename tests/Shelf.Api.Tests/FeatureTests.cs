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

    private async Task<BookResponse> CreateAsync(CreateBookRequest request)
    {
        var response = await _client.PostAsJsonAsync("/books", request, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
    }
}

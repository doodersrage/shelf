using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class AudioFeedsTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // A private feed opens the recording to any podcast app, a track an episode in order, signed in or not; a new
    // address stops the old one, stopping it stops it, and only the book's owner can make one.
    [Fact]
    public async Task An_audiobook_opens_as_a_private_podcast_feed()
    {
        var reader = await factory.SignUpAsync("Feed Maker");
        var book = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Heard in the Car", "Ann Writer", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        var paper = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Paper Only", "Ann Writer", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        var zip = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in new[] { ("01 Start.mp3", "first track for the feed"), ("02 End.mp3", "second track for the feed") })
            {
                await using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                await writer.WriteAsync(text);
            }
        }

        using (var content = new MultipartFormDataContent { { new ByteArrayContent(zip.ToArray()), "file", "car.zip" } })
        {
            await reader.PostAsync($"/books/{book.Id}/audio", content);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PostAsync($"/books/{paper.Id}/audio/feed", null)).StatusCode);
        var made = (await (await reader.PostAsync($"/books/{book.Id}/audio/feed", null)).Content.ReadFromJsonAsync<AudioFeedMade>(JsonOptions))!;
        var feedPath = new Uri(made.Url).PathAndQuery;
        Assert.True((await reader.GetFromJsonAsync<AudioFeedState>($"/books/{book.Id}/audio/feed", JsonOptions))!.On);

        var app = factory.CreateClient();
        var rss = XDocument.Parse(await app.GetStringAsync(feedPath));
        var channel = rss.Root!.Element("channel")!;
        Assert.Equal("Heard in the Car", channel.Element("title")!.Value);
        var items = channel.Elements("item").ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal("01 Start", items[0].Element("title")!.Value);
        var track = new Uri(items[1].Element("enclosure")!.Attribute("url")!.Value).PathAndQuery;
        Assert.Equal("second track for the feed", await app.GetStringAsync(track));

        // A new address stops the old one; stopping stops the new.
        var again = (await (await reader.PostAsync($"/books/{book.Id}/audio/feed", null)).Content.ReadFromJsonAsync<AudioFeedMade>(JsonOptions))!;
        Assert.Equal(HttpStatusCode.NotFound, (await app.GetAsync(feedPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.GetAsync(track)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync(new Uri(again.Url).PathAndQuery)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await reader.DeleteAsync($"/books/{book.Id}/audio/feed")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.GetAsync(new Uri(again.Url).PathAndQuery)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.GetAsync("/listen-feed/not-a-real-secret-at-all-0000/feed.xml")).StatusCode);

        // A page that plays it in any browser, under the same secret; and an address made to last a day stops when
        // its day is up.
        var shared = (await (await reader.PostAsJsonAsync($"/books/{book.Id}/audio/feed", new AudioFeedRequest(1), JsonOptions)).Content.ReadFromJsonAsync<AudioFeedMade>(JsonOptions))!;
        var page = await app.GetStringAsync(new Uri(shared.PageUrl).PathAndQuery);
        Assert.Contains("<h1>Heard in the Car</h1>", page);
        Assert.Contains("<audio controls", page);
        Assert.NotNull((await reader.GetFromJsonAsync<AudioFeedState>($"/books/{book.Id}/audio/feed", JsonOptions))!.Until);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Shelf.Api.Data.ShelfDb>();
            var feed = await db.AudioFeeds.IgnoreQueryFilters().SingleAsync(item => item.BookId == book.Id);
            feed.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.NotFound, (await app.GetAsync(new Uri(shared.PageUrl).PathAndQuery)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.GetAsync(new Uri(shared.Url).PathAndQuery)).StatusCode);
        Assert.False((await reader.GetFromJsonAsync<AudioFeedState>($"/books/{book.Id}/audio/feed", JsonOptions))!.On);

        // Nothing kept by caches; an odd character in a title doesn't stop the feed; no stored names in it; the page
        // gives the feed's whole address; and the address stops with the recording.
        var fresh = (await (await reader.PostAsync($"/books/{book.Id}/audio/feed", null)).Content.ReadFromJsonAsync<AudioFeedMade>(JsonOptions))!;
        await reader.PutAsJsonAsync($"/books/{book.Id}", new UpdateBookRequest("Heard\u0001 in the Car", "Ann Writer", BookStatus.Reading, null), JsonOptions);
        var answer = await app.GetAsync(new Uri(fresh.Url).PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains("no-store", answer.Headers.CacheControl!.ToString());
        var xml = await answer.Content.ReadAsStringAsync();
        Assert.Contains("<title>Heard in the Car</title>", xml);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var stored = (await scope.ServiceProvider.GetRequiredService<Shelf.Api.Data.ShelfDb>().Books.IgnoreQueryFilters().SingleAsync(item => item.Id == book.Id)).AudioStoredName!;
            Assert.DoesNotContain(stored, xml);
        }

        var sharePage = await app.GetAsync(new Uri(fresh.PageUrl).PathAndQuery);
        Assert.Contains("frame-ancestors 'none'", sharePage.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("http://localhost/listen-feed/", await sharePage.Content.ReadAsStringAsync());
        await reader.DeleteAsync($"/books/{book.Id}/audio");
        Assert.False((await reader.GetFromJsonAsync<AudioFeedState>($"/books/{book.Id}/audio/feed", JsonOptions))!.On);
        Assert.Equal(HttpStatusCode.NotFound, (await app.GetAsync(new Uri(fresh.Url).PathAndQuery)).StatusCode);

        var stranger = await factory.SignUpAsync("Feed Stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/books/{book.Id}/audio/feed", null)).StatusCode);
    }
}

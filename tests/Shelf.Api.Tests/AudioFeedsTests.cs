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

        var stranger = await factory.SignUpAsync("Feed Stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/books/{book.Id}/audio/feed", null)).StatusCode);
    }
}

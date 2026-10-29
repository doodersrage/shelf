using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class PlaylistsTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static async Task<BookResponse> BookAsync(HttpClient client, string title, bool audio)
    {
        var book = (await (await client.PostAsJsonAsync("/books", new CreateBookRequest(title, "Someone", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        if (audio)
        {
            using var content = new MultipartFormDataContent { { new StreamContent(BooksEndpointTests.ZipText("01.mp3", "a track of " + title)), "file", "heard.zip" } };
            await client.PostAsync($"/books/{book.Id}/audio", content);
        }

        return book;
    }

    private async Task<int> EpisodeAsync(string readerName, string feed)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var readerId = await factory.ReaderIdAsync(readerName);
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var podcast = new Podcast { OwnerId = readerId, FeedUrl = feed, Title = "The Show" };
        db.Podcasts.Add(podcast);
        await db.SaveChangesAsync();
        var episode = new PodcastEpisode { PodcastId = podcast.Id, Guid = "ep-1", Title = "Episode One", AudioUrl = "https://example.invalid/1.mp3" };
        db.PodcastEpisodes.Add(episode);
        await db.SaveChangesAsync();
        return episode.Id;
    }

    // Audiobooks and episodes together, in an order the reader sets; a book without a recording, or another
    // reader's playlist, can't be had; a book that loses its recording stays in its place, marked as passed over.
    [Fact]
    public async Task A_playlist_holds_books_and_episodes_in_the_readers_order()
    {
        var reader = await factory.SignUpAsync("Playlist Maker");
        var first = await BookAsync(reader, "First Heard", audio: true);
        var second = await BookAsync(reader, "Second Heard", audio: true);
        var paper = await BookAsync(reader, "Only Paper", audio: false);
        var episode = await EpisodeAsync("Playlist Maker", "https://example.invalid/feed.xml");

        var made = await reader.PostAsJsonAsync("/books/playlists", new PlaylistRequest("The Long Drive"), JsonOptions);
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var list = (await made.Content.ReadFromJsonAsync<PlaylistSummary>(JsonOptions))!;
        foreach (var add in new[] { new PlaylistAddRequest(first.Id, null), new PlaylistAddRequest(null, episode), new PlaylistAddRequest(second.Id, null) })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsJsonAsync($"/books/playlists/{list.Id}/items", add, JsonOptions)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PostAsJsonAsync($"/books/playlists/{list.Id}/items", new PlaylistAddRequest(paper.Id, null), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PostAsJsonAsync($"/books/playlists/{list.Id}/items", new PlaylistAddRequest(first.Id, null), JsonOptions)).StatusCode);

        var view = (await reader.GetFromJsonAsync<PlaylistView>($"/books/playlists/{list.Id}", JsonOptions))!;
        Assert.Equal(["First Heard", "Episode One", "Second Heard"], view.Items.Select(item => item.Title));
        Assert.Equal([$"/books/{first.Id}/audio/plan", $"/books/podcasts/episodes/{episode}/plan", $"/books/{second.Id}/audio/plan"], view.Items.Select(item => item.PlanUrl));

        // The last one moved up to the top, the episode taken out.
        Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsJsonAsync($"/books/playlists/{list.Id}/move", new PlaylistMoveRequest(view.Items[2].Id, -1), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsJsonAsync($"/books/playlists/{list.Id}/move", new PlaylistMoveRequest(view.Items[2].Id, -1), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await reader.DeleteAsync($"/books/playlists/{list.Id}/items/{view.Items[1].Id}")).StatusCode);
        view = (await reader.GetFromJsonAsync<PlaylistView>($"/books/playlists/{list.Id}", JsonOptions))!;
        Assert.Equal(["Second Heard", "First Heard"], view.Items.Select(item => item.Title));

        await reader.DeleteAsync($"/books/{first.Id}/audio");
        view = (await reader.GetFromJsonAsync<PlaylistView>($"/books/playlists/{list.Id}", JsonOptions))!;
        Assert.Equal([false, true], view.Items.Select(item => item.Gone));

        var stranger = await factory.SignUpAsync("Playlist Stranger");
        Assert.Empty((await stranger.GetFromJsonAsync<PlaylistSummary[]>("/books/playlists", JsonOptions))!);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/books/playlists/{list.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"/books/playlists/{list.Id}/items", new PlaylistAddRequest(second.Id, null), JsonOptions)).StatusCode);
        var theirs = (await (await stranger.PostAsJsonAsync("/books/playlists", new PlaylistRequest("Mine"), JsonOptions)).Content.ReadFromJsonAsync<PlaylistSummary>(JsonOptions))!;
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"/books/playlists/{theirs.Id}/items", new PlaylistAddRequest(second.Id, null), JsonOptions)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await reader.DeleteAsync($"/books/playlists/{list.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/books/{second.Id}")).StatusCode);
    }

    // A playlist goes out in the full export, and comes back with the books it held.
    [Fact]
    public async Task A_playlist_goes_out_in_the_export_and_comes_back()
    {
        var reader = await factory.SignUpAsync("Playlist Exporter");
        var book = await BookAsync(reader, "Exported Listening", audio: true);
        var list = (await (await reader.PostAsJsonAsync("/books/playlists", new PlaylistRequest("Commute"), JsonOptions)).Content.ReadFromJsonAsync<PlaylistSummary>(JsonOptions))!;
        await reader.PostAsJsonAsync($"/books/playlists/{list.Id}/items", new PlaylistAddRequest(book.Id, null), JsonOptions);

        var zip = await reader.GetByteArrayAsync("/books/export/full");
        using (var archive = new ZipArchive(new MemoryStream(zip)))
        {
            Assert.NotNull(archive.GetEntry("playlists.json"));
        }

        var elsewhere = await factory.SignUpAsync("Playlist Restorer");
        using (var content = new MultipartFormDataContent { { new ByteArrayContent(zip), "file", "backup.zip" } })
        {
            await elsewhere.PostAsync("/books/import/full", content);
        }

        var restored = Assert.Single((await elsewhere.GetFromJsonAsync<PlaylistSummary[]>("/books/playlists", JsonOptions))!);
        Assert.Equal(("Commute", 1), (restored.Name, restored.Count));
        Assert.Equal("Exported Listening", Assert.Single((await elsewhere.GetFromJsonAsync<PlaylistView>($"/books/playlists/{restored.Id}", JsonOptions))!.Items).Title);
    }
}

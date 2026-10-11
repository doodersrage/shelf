using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class PodcastTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static string Feed(int episodes, string title = "The Shelf Show") => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
          <channel>
            <title>{title}</title>
            <itunes:author>Shelf Radio</itunes:author>
            <description>&lt;p&gt;Talk about &lt;b&gt;books&lt;/b&gt;.&lt;/p&gt;</description>
            <itunes:image href="https://example.com/show.jpg" />
            {string.Concat(Enumerable.Range(1, episodes).Select(i => $"""
            <item>
              <title>Episode {i}</title>
              <guid>ep-{i}</guid>
              <pubDate>Mon, {i:00} Sep 2025 10:00:00 GMT</pubDate>
              <itunes:duration>{(i % 2 == 0 ? "1:02:03" : "1830")}</itunes:duration>
              <enclosure url="https://example.com/ep{i}.mp3" type="audio/mpeg" length="1000" />
            </item>
            """))}
            <item><title>Not audio</title><guid>video</guid><enclosure url="https://example.com/v.mp4" type="video/mp4" /></item>
          </channel>
        </rss>
        """;

    [Fact]
    public void A_feed_is_read_with_its_episodes_dates_and_durations()
    {
        var feed = PodcastFeeds.Read(Feed(2))!;
        Assert.Equal(("The Shelf Show", "Shelf Radio", "Talk about books .", "https://example.com/show.jpg"), (feed.Title, feed.Author, feed.Description, feed.ImageUrl));
        Assert.Equal(["ep-1", "ep-2"], feed.Episodes.Select(episode => episode.Guid));
        Assert.Equal((1830, 3723), (feed.Episodes[0].DurationSeconds, feed.Episodes[1].DurationSeconds));
        Assert.Equal(new DateTimeOffset(2025, 9, 2, 10, 0, 0, TimeSpan.Zero), feed.Episodes[1].PublishedAt);
        Assert.Equal(new DateTimeOffset(2025, 1, 3, 9, 0, 0, TimeSpan.FromHours(-5)), PodcastFeeds.Date("Fri, 3 Jan 2025 09:00:00 EST"));

        var atom = PodcastFeeds.Read("""
            <feed xmlns="http://www.w3.org/2005/Atom"><title>Atom Cast</title>
            <entry><id>a1</id><title>One</title><published>2025-02-01T00:00:00Z</published><link rel="enclosure" type="audio/mpeg" href="https://example.com/a1.mp3" /></entry></feed>
            """)!;
        Assert.Equal(("Atom Cast", "a1", "https://example.com/a1.mp3"), (atom.Title, atom.Episodes[0].Guid, atom.Episodes[0].AudioUrl));
        Assert.Null(PodcastFeeds.Read("<html>not a feed"));
        Assert.Equal(["https://example.com/feed.xml"], PodcastFeeds.Opml("""<opml><body><outline text="x" xmlUrl="https://example.com/feed.xml"/><outline text="bad" xmlUrl="javascript:alert(1)"/></body></opml>"""));
    }

    [Fact]
    public async Task Following_a_podcast_keeps_the_newest_episodes_and_plays_them_with_a_place()
    {
        var client = await factory.SignUpAsync("Podcast Listener");
        var readerId = await factory.ReaderIdAsync("Podcast Listener");
        factory.PodcastFeeds.Feeds["https://example.com/show.xml"] = Feed(6);
        var service = factory.Services.GetRequiredService<PodcastService>();

        var followed = await client.PostAsJsonAsync("/books/podcasts", new SubscribeRequest("https://example.com/show.xml"), JsonOptions);
        followed.EnsureSuccessStatusCode();
        var podcast = (await followed.Content.ReadFromJsonAsync<PodcastSummary>(JsonOptions))!;
        // Of the six already out, the newest three wait to be heard, and are downloaded.
        Assert.Equal(("The Shelf Show", 3), (podcast.Title, podcast.Unplayed));
        var episodes = (await client.GetFromJsonAsync<EpisodeRow[]>($"/books/podcasts/{podcast.Id}/episodes", JsonOptions))!;
        Assert.Equal(["Episode 6", "Episode 5", "Episode 4"], episodes.Where(item => !item.Played).Select(item => item.Title));
        Assert.Equal(3, episodes.Count(item => item.Downloaded));

        // Played in the player books use, from the shelf, with the place kept.
        var newest = episodes[0];
        var plan = (await client.GetFromJsonAsync<EpisodePlan>($"/books/podcasts/episodes/{newest.Id}/plan", JsonOptions))!;
        Assert.Equal(($"episode-{newest.Id}", "Episode 6", "The Shelf Show", $"/books/podcasts/episodes/{newest.Id}/place"), (plan.Key, plan.Title, plan.Author, plan.PlaceUrl));
        var audio = await client.GetAsync(plan.Tracks[0].Url);
        Assert.Equal(("audio/mpeg", "episode 6 audio"), (audio.Content.Headers.ContentType?.MediaType, await audio.Content.ReadAsStringAsync()));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(plan.PlaceUrl, new AudioPlaceRequest(0, 42), JsonOptions)).StatusCode);
        Assert.Equal(42, (await client.GetFromJsonAsync<EpisodePlan>($"/books/podcasts/episodes/{newest.Id}/plan", JsonOptions))!.Seconds);

        // Heard to the end; a new episode arrives and is downloaded; played ones go after a day.
        (await client.PostAsync(plan.FinishedUrl, null)).EnsureSuccessStatusCode();
        factory.PodcastFeeds.Feeds["https://example.com/show.xml"] = Feed(7);
        await using (var scope = Scope(readerId))
        {
            Assert.Equal(1, await service.RefreshAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), podcast.Id));
        }

        episodes = (await client.GetFromJsonAsync<EpisodeRow[]>($"/books/podcasts/{podcast.Id}/episodes", JsonOptions))!;
        Assert.Equal(("Episode 7", false, true), (episodes[0].Title, episodes[0].Played, episodes[0].Downloaded));
        Assert.True(episodes.Single(item => item.Title == "Episode 6").Played);

        // Another reader sees none of it; OPML carries the list away.
        var other = await factory.SignUpAsync("Podcast Other");
        Assert.Empty((await other.GetFromJsonAsync<PodcastSummary[]>("/books/podcasts", JsonOptions))!);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/books/podcasts/episodes/{newest.Id}/plan")).StatusCode);
        Assert.Contains("xmlUrl=\"https://example.com/show.xml\"", await client.GetStringAsync("/books/podcasts/opml"), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/books/podcasts", new SubscribeRequest("https://example.com/not-a-feed"), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/books/podcasts/{podcast.Id}")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<PodcastSummary[]>("/books/podcasts", JsonOptions))!);
    }

    private AsyncServiceScope Scope(int readerId)
    {
        var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
        return scope;
    }
}

// Feeds by address, and downloads that write "episode N audio".
public sealed class StubPodcastClient : IPodcastClient
{
    public ConcurrentDictionary<string, string> Feeds { get; } = new();

    public Task<string?> FeedAsync(string url, CancellationToken cancellationToken) => Task.FromResult(Feeds.TryGetValue(url, out var xml) ? xml : null);

    public Task<IReadOnlyList<PodcastFound>> SearchAsync(string term, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PodcastFound>>([new PodcastFound("The Shelf Show", "Shelf Radio", "https://example.com/show.xml", null)]);

    public async Task<bool> DownloadAsync(string url, string path, long mostBytes, CancellationToken cancellationToken)
    {
        var number = System.Text.RegularExpressions.Regex.Match(url, @"ep(\d+)").Groups[1].Value;
        await File.WriteAllTextAsync(path, $"episode {number} audio", cancellationToken);
        return true;
    }
}

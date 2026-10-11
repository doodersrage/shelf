using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// A podcast a reader follows, by its feed.
public sealed class Podcast
{
    public const int MaxKeep = 20;

    public int Id { get; set; }
    public int OwnerId { get; set; }
    public required string FeedUrl { get; set; }
    public required string Title { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }

    // How many of the newest episodes not yet played are kept downloaded; 0 downloads none on its own.
    public int KeepLatest { get; set; } = 3;
    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
    public string? Problem { get; set; }
    public List<PodcastEpisode> Episodes { get; set; } = [];
}

public sealed class PodcastEpisode
{
    public int Id { get; set; }
    public int PodcastId { get; set; }
    public Podcast? Podcast { get; set; }

    // The feed's own id for the episode, so it is found once however often the feed is read.
    public required string Guid { get; set; }
    public required string Title { get; set; }
    public string? Summary { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public required string AudioUrl { get; set; }
    public string? ContentType { get; set; }
    public int? DurationSeconds { get; set; }

    // The file kept on the shelf, once downloaded.
    public string? StoredName { get; set; }

    // Where the listener stopped, and whether they heard it to the end.
    public double Seconds { get; set; }
    public bool Played { get; set; }
    public DateTimeOffset? PlayedAt { get; set; }
    public DateTimeOffset FoundAt { get; set; }
}

public sealed record PodcastFeed(string Title, string? Author, string? Description, string? ImageUrl, IReadOnlyList<FeedEpisode> Episodes);

public sealed record FeedEpisode(string Guid, string Title, string? Summary, DateTimeOffset? PublishedAt, string AudioUrl, string? ContentType, int? DurationSeconds);

public sealed record PodcastFound(string Title, string? Author, string FeedUrl, string? ImageUrl);

public sealed record PodcastSummary(int Id, string Title, string? Author, string? ImageUrl, int Unplayed, DateTimeOffset? Latest, int KeepLatest, string? Problem);

public sealed record EpisodeRow(int Id, string Title, string? Summary, DateTimeOffset? PublishedAt, int? DurationSeconds, double Seconds, bool Played, bool Downloaded);

public sealed record SubscribeRequest(string FeedUrl);

public sealed record EpisodePlan(
    int BookId,
    string Key,
    string Title,
    string Author,
    string? Cover,
    AudioPlanTrack[] Tracks,
    AudioPlanChapter[] Chapters,
    int Track,
    double Seconds,
    int Speed,
    bool HasEbook,
    string PlaceUrl,
    string FinishedUrl,
    string Link,
    string PlanUrl);

public interface IPodcastClient
{
    Task<string?> FeedAsync(string url, CancellationToken cancellationToken);

    Task<IReadOnlyList<PodcastFound>> SearchAsync(string term, CancellationToken cancellationToken);

    // Downloads to a file; false when it could not, or it was larger than allowed.
    Task<bool> DownloadAsync(string url, string path, long mostBytes, CancellationToken cancellationToken);
}

public sealed class PodcastClient(HttpClient http) : IPodcastClient
{
    public const long MostFeedBytes = 20L * 1024 * 1024;

    // The client that passes an episode through to the player, for one whose address is not HTTPS.
    public const string StreamName = "podcast-stream";

    public async Task<string?> FeedAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var answer = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!answer.IsSuccessStatusCode || answer.Content.Headers.ContentLength > MostFeedBytes)
            {
                return null;
            }

            return await answer.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    // Apple's podcast directory, which needs no account and finds most podcasts' feeds.
    public async Task<IReadOnlyList<PodcastFound>> SearchAsync(string term, CancellationToken cancellationToken)
    {
        try
        {
            using var answer = await http.GetAsync($"https://itunes.apple.com/search?media=podcast&entity=podcast&limit=20&term={Uri.EscapeDataString(term)}", cancellationToken);
            if (!answer.IsSuccessStatusCode)
            {
                return [];
            }

            var json = await answer.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return json.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
                ? results.EnumerateArray()
                    .Where(item => item.TryGetProperty("feedUrl", out var feed) && feed.ValueKind == JsonValueKind.String)
                    .Select(item => new PodcastFound(
                        item.TryGetProperty("collectionName", out var name) ? name.GetString() ?? "" : "",
                        item.TryGetProperty("artistName", out var artist) ? artist.GetString() : null,
                        item.GetProperty("feedUrl").GetString()!,
                        item.TryGetProperty("artworkUrl600", out var art) ? art.GetString() : null))
                    .ToList()
                : [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return [];
        }
    }

    public async Task<bool> DownloadAsync(string url, string path, long mostBytes, CancellationToken cancellationToken)
    {
        var partial = path + ".part";
        try
        {
            using var answer = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!answer.IsSuccessStatusCode || answer.Content.Headers.ContentLength > mostBytes)
            {
                return false;
            }

            await using (var source = await answer.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = File.Create(partial))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > mostBytes)
                    {
                        return false;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            File.Move(partial, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return false;
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }
}

public static partial class PodcastFeeds
{
    public const int MostEpisodes = 1000;

    // An RSS feed (with Apple's podcast tags, as nearly all have) or an Atom one; null when it is neither.
    public static PodcastFeed? Read(string xml)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        XNamespace itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
        XNamespace atom = "http://www.w3.org/2005/Atom";
        var channel = document.Root?.Element("channel");
        if (channel is not null)
        {
            var episodes = channel.Elements("item").Select(item =>
            {
                var enclosure = item.Element("enclosure");
                var url = enclosure?.Attribute("url")?.Value;
                if (string.IsNullOrWhiteSpace(url) || !IsAudio(enclosure!.Attribute("type")?.Value, url))
                {
                    return null;
                }

                return new FeedEpisode(
                    Fit(item.Element("guid")?.Value is { Length: > 0 } guid ? guid : url, 500),
                    Fit(item.Element("title")?.Value ?? "", 300),
                    Plain(item.Element(itunes + "summary")?.Value ?? item.Element("description")?.Value),
                    Date(item.Element("pubDate")?.Value),
                    url.Trim(),
                    enclosure.Attribute("type")?.Value,
                    Duration(item.Element(itunes + "duration")?.Value));
            }).OfType<FeedEpisode>().Take(MostEpisodes).ToList();
            return new PodcastFeed(
                Fit(channel.Element("title")?.Value ?? "", 300),
                Nullable(channel.Element(itunes + "author")?.Value, 200),
                Plain(channel.Element(itunes + "summary")?.Value ?? channel.Element("description")?.Value),
                channel.Element(itunes + "image")?.Attribute("href")?.Value ?? channel.Element("image")?.Element("url")?.Value,
                episodes);
        }

        if (document.Root?.Name == atom + "feed")
        {
            var feed = document.Root;
            var episodes = feed.Elements(atom + "entry").Select(entry =>
            {
                var link = entry.Elements(atom + "link").FirstOrDefault(item => item.Attribute("rel")?.Value == "enclosure");
                var url = link?.Attribute("href")?.Value;
                if (string.IsNullOrWhiteSpace(url) || !IsAudio(link!.Attribute("type")?.Value, url))
                {
                    return null;
                }

                return new FeedEpisode(
                    Fit(entry.Element(atom + "id")?.Value ?? url, 500),
                    Fit(entry.Element(atom + "title")?.Value ?? "", 300),
                    Plain(entry.Element(atom + "summary")?.Value ?? entry.Element(atom + "content")?.Value),
                    Date(entry.Element(atom + "published")?.Value ?? entry.Element(atom + "updated")?.Value),
                    url.Trim(),
                    link.Attribute("type")?.Value,
                    null);
            }).OfType<FeedEpisode>().Take(MostEpisodes).ToList();
            return new PodcastFeed(Fit(feed.Element(atom + "title")?.Value ?? "", 300), Nullable(feed.Element(atom + "author")?.Element(atom + "name")?.Value, 200),
                Plain(feed.Element(atom + "subtitle")?.Value), feed.Element(atom + "logo")?.Value ?? feed.Element(atom + "icon")?.Value, episodes);
        }

        return null;
    }

    // The feeds in an OPML file, as podcast apps export them.
    public static IReadOnlyList<string> Opml(string xml)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            return XDocument.Load(reader).Descendants("outline").Select(outline => outline.Attribute("xmlUrl")?.Value).OfType<string>()
                .Where(url => Notices.IsWebAddress(url, out _)).Distinct().ToList();
        }
        catch (XmlException)
        {
            return [];
        }
    }

    public static string ToOpml(IEnumerable<(string Title, string FeedUrl)> podcasts) =>
        new XDocument(new XElement("opml", new XAttribute("version", "2.0"),
            new XElement("head", new XElement("title", "Shelf podcasts")),
            new XElement("body", podcasts.Select(podcast => new XElement("outline", new XAttribute("type", "rss"), new XAttribute("text", podcast.Title), new XAttribute("title", podcast.Title), new XAttribute("xmlUrl", podcast.FeedUrl))))))
            .ToString();

    private static bool IsAudio(string? type, string url) =>
        type?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true
        || (type is null or "" && Path.GetExtension(new Uri(url, UriKind.RelativeOrAbsolute).IsAbsoluteUri ? new Uri(url).AbsolutePath : url).ToLowerInvariant() is ".mp3" or ".m4a" or ".aac" or ".ogg" or ".opus");

    // "01:02:03", "62:03", or seconds.
    public static int? Duration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Trim().Split(':');
        var seconds = 0.0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return null;
            }

            seconds = (seconds * 60) + number;
        }

        return seconds is > 0 and < 1_000_000 ? (int)seconds : null;
    }

    // RFC 822 dates as feeds write them, with names of time zones some use. The day of the week is left out, as
    // feeds often have it wrong, which would make the whole date fail.
    public static DateTimeOffset? Date(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var cleaned = Zone().Replace(Weekday().Replace(text.Trim(), ""), match => match.Value switch
        {
            "GMT" or "UT" or "Z" => "+0000",
            "EST" => "-0500",
            "EDT" => "-0400",
            "CST" => "-0600",
            "CDT" => "-0500",
            "MST" => "-0700",
            "MDT" => "-0600",
            "PST" => "-0800",
            "PDT" => "-0700",
            _ => match.Value,
        });
        return DateTimeOffset.TryParse(cleaned, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date
            : DateTimeOffset.TryParseExact(cleaned, ["d MMM yyyy HH:mm:ss zzz", "d MMM yyyy HH:mm zzz", "d MMM yyyy HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date) ? date
            : null;
    }

    private static string? Plain(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        return Nullable(Spaces().Replace(WebUtility.HtmlDecode(Tags().Replace(html, " ")), " ").Trim(), 4000);
    }

    private static string Fit(string text, int most) => text.Trim() is var trimmed && trimmed.Length > most ? trimmed[..most] : text.Trim();

    private static string? Nullable(string? text, int most) => string.IsNullOrWhiteSpace(text) ? null : Fit(text, most);

    [GeneratedRegex(@"^[A-Za-z]+,\s*")]
    private static partial Regex Weekday();

    [GeneratedRegex(@"\b(GMT|UT|Z|EST|EDT|CST|CDT|MST|MDT|PST|PDT)$")]
    private static partial Regex Zone();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

// Podcasts: following feeds, keeping the newest episodes downloaded, and playing them in the player books use.
public sealed class PodcastService(IServiceScopeFactory scopes, IPodcastClient client, AudioStore audio, ILogger<PodcastService> logger) : BackgroundService
{
    public const int MostPodcasts = 200;
    public static long MostEpisodeBytes => AudioStore.MaxBytes;

    public string Root => Path.Combine(audio.Root, "podcasts");

    public static void Map(IEndpointRouteBuilder app)
    {
        var podcasts = app.MapGroup("/books/podcasts").WithTags("Podcasts").RequireAuthorization();
        podcasts.MapGet("/", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await ListAsync(db, cancellationToken)));
        podcasts.MapPost("/", async Task<Results<Ok<PodcastSummary>, ValidationProblem>> (SubscribeRequest request, ShelfDb db, PodcastService service, CancellationToken cancellationToken) =>
            await service.SubscribeAsync(db, request.FeedUrl, cancellationToken) is { Podcast: { } made } ? TypedResults.Ok((await ListAsync(db, cancellationToken)).First(item => item.Id == made))
                : TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["feedUrl"] = [T("That address is not a podcast's feed Shelf can read.")] }));
        podcasts.MapGet("/search", async (string? q, PodcastService service, CancellationToken cancellationToken) => TypedResults.Ok(await service.SearchAsync(q, cancellationToken)));
        podcasts.MapGet("/opml", async (ShelfDb db, CancellationToken cancellationToken) =>
            Results.File(System.Text.Encoding.UTF8.GetBytes(PodcastFeeds.ToOpml(await db.Podcasts.AsNoTracking().OrderBy(item => item.Title).Select(item => new ValueTuple<string, string>(item.Title, item.FeedUrl)).ToListAsync(cancellationToken))), "text/x-opml", "shelf-podcasts.opml"));
        podcasts.MapGet("/{id:int}/episodes", async Task<Results<Ok<EpisodeRow[]>, NotFound>> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await EpisodesAsync(db, id, cancellationToken) is { } episodes ? TypedResults.Ok(episodes) : TypedResults.NotFound());
        podcasts.MapDelete("/{id:int}", async Task<Results<NoContent, NotFound>> (int id, ShelfDb db, PodcastService service, CancellationToken cancellationToken) =>
            await service.UnsubscribeAsync(db, id, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());
        podcasts.MapGet("/episodes/{id:int}/plan", async Task<Results<Ok<EpisodePlan>, NotFound>> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await PlanAsync(db, id, cancellationToken) is { } plan ? TypedResults.Ok(plan) : TypedResults.NotFound());
        podcasts.MapGet("/episodes/{id:int}/audio", Audio);
        podcasts.MapPut("/episodes/{id:int}/place", async Task<Results<NoContent, NotFound>> (int id, AudioPlaceRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            await KeepPlaceAsync(db, id, request.Seconds, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());
        podcasts.MapPost("/episodes/{id:int}/finished", async Task<Results<Ok<FinishedResponse>, NotFound>> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await SetPlayedAsync(db, id, true, cancellationToken) ? TypedResults.Ok(new FinishedResponse(true)) : TypedResults.NotFound());
    }

    private static IQueryable<PodcastEpisode> Mine(ShelfDb db) => db.PodcastEpisodes.Where(episode => episode.Podcast!.OwnerId == db.ReaderId && db.ReaderId != 0);

    // SQLite cannot compare the dates, so the newest is found here.
    public static async Task<PodcastSummary[]> ListAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var podcasts = await db.Podcasts.AsNoTracking().ToListAsync(cancellationToken);
        var ids = podcasts.Select(podcast => podcast.Id).ToList();
        var episodes = (await db.PodcastEpisodes.AsNoTracking().Where(episode => ids.Contains(episode.PodcastId))
            .Select(episode => new { episode.PodcastId, episode.Played, episode.PublishedAt }).ToListAsync(cancellationToken))
            .ToLookup(episode => episode.PodcastId);
        return podcasts
            .Select(podcast => new PodcastSummary(podcast.Id, podcast.Title, podcast.Author, podcast.ImageUrl, episodes[podcast.Id].Count(episode => !episode.Played),
                episodes[podcast.Id].Max(episode => episode.PublishedAt), podcast.KeepLatest, podcast.Problem))
            .OrderByDescending(item => item.Latest ?? DateTimeOffset.MinValue)
            .ToArray();
    }

    public static async Task<EpisodeRow[]?> EpisodesAsync(ShelfDb db, int podcastId, CancellationToken cancellationToken = default)
    {
        if (!await db.Podcasts.AnyAsync(podcast => podcast.Id == podcastId, cancellationToken))
        {
            return null;
        }

        return (await Mine(db).AsNoTracking().Where(episode => episode.PodcastId == podcastId).ToListAsync(cancellationToken))
            .OrderByDescending(episode => episode.PublishedAt ?? episode.FoundAt)
            .Select(episode => new EpisodeRow(episode.Id, episode.Title, episode.Summary, episode.PublishedAt, episode.DurationSeconds, episode.Seconds, episode.Played, episode.StoredName is not null))
            .ToArray();
    }

    public async Task<IReadOnlyList<PodcastFound>> SearchAsync(string? term, CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(term) || term.Trim().Length > 200 ? [] : await client.SearchAsync(term.Trim(), cancellationToken);

    public async Task<(int? Podcast, string? Problem)> SubscribeAsync(ShelfDb db, string? feedUrl, CancellationToken cancellationToken = default)
    {
        if (await Family.IsRestrictedAsync(db, cancellationToken: cancellationToken))
        {
            return (null, T("A child's account cannot do that. Ask the grown-up who made it."));
        }

        if (!Notices.IsWebAddress(feedUrl, out var address) || address.AbsoluteUri.Length > 1000)
        {
            return (null, T("That address is not a podcast's feed Shelf can read."));
        }

        if (await db.Podcasts.FirstOrDefaultAsync(podcast => podcast.FeedUrl == address.AbsoluteUri, cancellationToken) is { } already)
        {
            return (already.Id, null);
        }

        if (await db.Podcasts.CountAsync(cancellationToken) >= MostPodcasts)
        {
            return (null, T("You follow {0} podcasts, the most there can be.", MostPodcasts));
        }

        if (await client.FeedAsync(address.AbsoluteUri, cancellationToken) is not { } xml || PodcastFeeds.Read(xml) is not { } feed || feed.Title.Length == 0)
        {
            return (null, T("That address is not a podcast's feed Shelf can read."));
        }

        var podcast = new Podcast { OwnerId = db.ReaderId, FeedUrl = address.AbsoluteUri, Title = feed.Title, AddedAt = DateTimeOffset.UtcNow };
        db.Podcasts.Add(podcast);
        Apply(podcast, feed, firstTime: true);
        await db.SaveChangesAsync(cancellationToken);
        await KeepDownloadedAsync(db, podcast.Id, cancellationToken);
        return (podcast.Id, null);
    }

    // The feeds in an OPML file, one after another; returns how many were followed.
    public async Task<(int Followed, int Failed)> ImportOpmlAsync(ShelfDb db, string xml, CancellationToken cancellationToken = default)
    {
        int followed = 0, failed = 0;
        foreach (var url in PodcastFeeds.Opml(xml).Take(MostPodcasts))
        {
            if ((await SubscribeAsync(db, url, cancellationToken)).Podcast is not null)
            {
                followed++;
            }
            else
            {
                failed++;
            }
        }

        return (followed, failed);
    }

    public async Task<bool> UnsubscribeAsync(ShelfDb db, int id, CancellationToken cancellationToken = default)
    {
        var podcast = await db.Podcasts.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (podcast is null)
        {
            return false;
        }

        db.Podcasts.Remove(podcast);
        await db.SaveChangesAsync(cancellationToken);
        DeleteFolder(id);
        return true;
    }

    public static async Task<bool> SetKeepAsync(ShelfDb db, int id, int keep, CancellationToken cancellationToken = default) =>
        await db.Podcasts.Where(item => item.Id == id).ExecuteUpdateAsync(set => set.SetProperty(item => item.KeepLatest, Math.Clamp(keep, 0, Podcast.MaxKeep)), cancellationToken) > 0;

    public static async Task<bool> SetPlayedAsync(ShelfDb db, int episodeId, bool played, CancellationToken cancellationToken = default)
    {
        var episode = await Mine(db).FirstOrDefaultAsync(item => item.Id == episodeId, cancellationToken);
        if (episode is null)
        {
            return false;
        }

        (episode.Played, episode.PlayedAt, episode.Seconds) = (played, played ? DateTimeOffset.UtcNow : null, 0);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public static async Task<bool> KeepPlaceAsync(ShelfDb db, int episodeId, double seconds, CancellationToken cancellationToken = default) =>
        !double.IsNaN(seconds) && seconds is >= 0 and < 1_000_000
        && await Mine(db).Where(item => item.Id == episodeId).ExecuteUpdateAsync(set => set.SetProperty(item => item.Seconds, seconds), cancellationToken) > 0;

    public static async Task<EpisodePlan?> PlanAsync(ShelfDb db, int episodeId, CancellationToken cancellationToken = default)
    {
        var episode = await Mine(db).AsNoTracking().Include(item => item.Podcast).FirstOrDefaultAsync(item => item.Id == episodeId, cancellationToken);
        if (episode is null)
        {
            return null;
        }

        var url = $"/books/podcasts/episodes/{episode.Id}";
        return new EpisodePlan(
            0,
            $"episode-{episode.Id}",
            episode.Title,
            episode.Podcast!.Title,
            episode.Podcast.ImageUrl is { } image && image.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? image : null,
            [new AudioPlanTrack(0, episode.Title, episode.DurationSeconds, url + "/audio")],
            [new AudioPlanChapter(0, episode.Title, 0, 0)],
            0,
            episode.Played ? 0 : episode.Seconds,
            await BookRules.GetAudioSpeedAsync(db, cancellationToken),
            false,
            url + "/place",
            url + "/finished",
            $"/podcasts/{episode.PodcastId}",
            url + "/plan");
    }

    // A downloaded episode comes from the shelf; one not downloaded from its own address, when that is HTTPS, or
    // through the shelf, which a page served over HTTPS needs for one that is not.
    private static async Task<IResult> Audio(int id, ShelfDb db, PodcastService service, HttpContext http, IHttpClientFactory clients, CancellationToken cancellationToken)
    {
        var episode = await Mine(db).AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (episode is null)
        {
            return Results.NotFound();
        }

        if (service.PathOf(episode) is { } path)
        {
            return Results.File(path, episode.ContentType is { Length: > 0 } type && type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? type : AudioStore.ContentType(path), enableRangeProcessing: true);
        }

        if (episode.AudioUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Redirect(episode.AudioUrl);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, episode.AudioUrl);
        if (http.Request.Headers.Range is { Count: > 0 } range)
        {
            request.Headers.TryAddWithoutValidation("Range", range.ToString());
        }

        var answer = await clients.CreateClient(PodcastClient.StreamName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        http.Response.RegisterForDispose(answer);
        http.Response.StatusCode = (int)answer.StatusCode;
        foreach (var header in new[] { "Content-Range", "Accept-Ranges" })
        {
            if (answer.Headers.TryGetValues(header, out var values) || answer.Content.Headers.TryGetValues(header, out values))
            {
                http.Response.Headers[header] = values.ToArray();
            }
        }

        http.Response.ContentType = answer.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";
        http.Response.ContentLength = answer.Content.Headers.ContentLength;
        await using var body = await answer.Content.ReadAsStreamAsync(cancellationToken);
        await body.CopyToAsync(http.Response.Body, cancellationToken);
        return Results.Empty;
    }

    public string? PathOf(PodcastEpisode episode)
    {
        if (episode.StoredName is not { } stored || Path.GetFileName(stored) != stored)
        {
            return null;
        }

        var path = Path.Combine(Root, episode.PodcastId.ToString(CultureInfo.InvariantCulture), stored);
        return File.Exists(path) ? path : null;
    }

    public async Task<bool> DownloadAsync(ShelfDb db, int episodeId, CancellationToken cancellationToken = default)
    {
        var episode = await Mine(db).FirstOrDefaultAsync(item => item.Id == episodeId, cancellationToken);
        if (episode is null || !await FetchAsync(episode, cancellationToken))
        {
            return false;
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Brings an episode's file to the shelf, unless it is here already.
    private async Task<bool> FetchAsync(PodcastEpisode episode, CancellationToken cancellationToken)
    {
        if (PathOf(episode) is not null)
        {
            return true;
        }

        var folder = Path.Combine(Root, episode.PodcastId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        var extension = Path.GetExtension(new Uri(episode.AudioUrl).AbsolutePath).ToLowerInvariant() is { Length: > 1 and <= 5 } found && found.Skip(1).All(char.IsLetterOrDigit) ? found : ".mp3";
        var stored = $"{episode.Id}{extension}";
        if (!await client.DownloadAsync(episode.AudioUrl, Path.Combine(folder, stored), MostEpisodeBytes, cancellationToken))
        {
            return false;
        }

        // Past the owner's storage limit, the download goes again.
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var owner = await db.Podcasts.IgnoreQueryFilters().Where(podcast => podcast.Id == episode.PodcastId).Select(podcast => podcast.OwnerId).FirstAsync(cancellationToken);
            if (!await scope.ServiceProvider.GetRequiredService<Storage>().FitsAsync(db, owner, 0, cancellationToken))
            {
                File.Delete(Path.Combine(folder, stored));
                return false;
            }
        }

        episode.StoredName = stored;
        return true;
    }

    public async Task<bool> ForgetDownloadAsync(ShelfDb db, int episodeId, CancellationToken cancellationToken = default)
    {
        var episode = await Mine(db).FirstOrDefaultAsync(item => item.Id == episodeId, cancellationToken);
        if (episode is null)
        {
            return false;
        }

        if (PathOf(episode) is { } path)
        {
            File.Delete(path);
        }

        episode.StoredName = null;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // New episodes from the feed. The first time, those already out count as heard but the newest few, so a podcast
    // with years of episodes does not arrive with hundreds waiting.
    private static int Apply(Podcast podcast, PodcastFeed feed, bool firstTime)
    {
        podcast.Title = feed.Title.Length > 0 ? feed.Title : podcast.Title;
        (podcast.Author, podcast.Description, podcast.ImageUrl) = (feed.Author, feed.Description, feed.ImageUrl is { Length: <= 1000 } image ? image : null);
        (podcast.CheckedAt, podcast.Problem) = (DateTimeOffset.UtcNow, null);
        var known = podcast.Episodes.Select(episode => episode.Guid).ToHashSet(StringComparer.Ordinal);
        var added = 0;
        var newest = feed.Episodes.OrderByDescending(episode => episode.PublishedAt ?? DateTimeOffset.MinValue).ToList();
        foreach (var (item, rank) in newest.Select((item, rank) => (item, rank)))
        {
            if (!known.Add(item.Guid) || item.AudioUrl.Length > 2000 || !Notices.IsWebAddress(item.AudioUrl, out _))
            {
                continue;
            }

            var heard = firstTime && rank >= Math.Max(3, podcast.KeepLatest);
            podcast.Episodes.Add(new PodcastEpisode
            {
                Guid = item.Guid,
                Title = item.Title.Length > 0 ? item.Title : T("Episode"),
                Summary = item.Summary,
                PublishedAt = item.PublishedAt,
                AudioUrl = item.AudioUrl,
                ContentType = item.ContentType is { Length: <= 100 } type ? type : null,
                DurationSeconds = item.DurationSeconds,
                Played = heard,
                PlayedAt = heard ? DateTimeOffset.UtcNow : null,
                FoundAt = DateTimeOffset.UtcNow,
            });
            added++;
        }

        return added;
    }

    // Reads one podcast's feed again; returns how many episodes are new.
    public async Task<int?> RefreshAsync(ShelfDb db, int podcastId, CancellationToken cancellationToken = default)
    {
        var podcast = await db.Podcasts.IgnoreQueryFilters().Include(item => item.Episodes).FirstOrDefaultAsync(item => item.Id == podcastId, cancellationToken);
        if (podcast is null)
        {
            return null;
        }

        if (await client.FeedAsync(podcast.FeedUrl, cancellationToken) is not { } xml || PodcastFeeds.Read(xml) is not { } feed)
        {
            (podcast.CheckedAt, podcast.Problem) = (DateTimeOffset.UtcNow, Say("The feed could not be read the last time Shelf looked."));
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        var added = Apply(podcast, feed, firstTime: false);
        await db.SaveChangesAsync(cancellationToken);
        await KeepDownloadedAsync(db, podcastId, cancellationToken);
        return added;
    }

    // The newest unplayed episodes stay downloaded, as many as the podcast keeps; played ones go once they are not.
    public async Task KeepDownloadedAsync(ShelfDb db, int podcastId, CancellationToken cancellationToken = default)
    {
        var podcast = await db.Podcasts.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(item => item.Id == podcastId, cancellationToken);
        if (podcast is null)
        {
            return;
        }

        var episodes = (await db.PodcastEpisodes.Where(item => item.PodcastId == podcastId).ToListAsync(cancellationToken))
            .OrderByDescending(item => item.PublishedAt ?? item.FoundAt).ToList();
        var wanted = episodes.Where(item => !item.Played).Take(podcast.KeepLatest).ToList();
        foreach (var episode in wanted)
        {
            await FetchAsync(episode, cancellationToken);
        }

        foreach (var episode in episodes.Where(item => item.Played && item.StoredName is not null && (item.PlayedAt is null || item.PlayedAt < DateTimeOffset.UtcNow.AddDays(-1))))
        {
            if (PathOf(episode) is { } path)
            {
                File.Delete(path);
            }

            episode.StoredName = null;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    // Every hour, each feed is read again, and folders left by podcasts no longer followed are cleared away.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await RefreshDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or IOException)
            {
                logger.LogWarning(ex, "Could not read podcast feeds; they will be read again in an hour.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RefreshDueAsync(CancellationToken cancellationToken)
    {
        List<int> due;
        HashSet<int> all;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var since = DateTimeOffset.UtcNow.AddMinutes(-50);
            var podcasts = await db.Podcasts.IgnoreQueryFilters().AsNoTracking().Select(item => new { item.Id, item.CheckedAt }).ToListAsync(cancellationToken);
            due = podcasts.Where(item => item.CheckedAt is null || item.CheckedAt < since).Select(item => item.Id).ToList();
            all = podcasts.Select(item => item.Id).ToHashSet();
        }

        foreach (var id in due)
        {
            await using var scope = scopes.CreateAsyncScope();
            await RefreshAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), id, cancellationToken);
        }

        if (Directory.Exists(Root))
        {
            foreach (var folder in Directory.GetDirectories(Root))
            {
                if (int.TryParse(Path.GetFileName(folder), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && !all.Contains(id))
                {
                    DeleteFolder(id);
                }
            }
        }
    }

    private void DeleteFolder(int podcastId)
    {
        var folder = Path.Combine(Root, podcastId.ToString(CultureInfo.InvariantCulture));
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

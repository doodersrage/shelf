using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

public sealed record ShelfRelease(string Version, string Url, DateTimeOffset? PublishedAt);

// Whether a newer Shelf has been released, asked of GitHub twice a day (Updates:Check, on by default; Updates:Url for
// a fork's own). Only the release's number and page are read; nothing about this shelf is sent.
public sealed class ReleaseWatch(IHttpClientFactory clients, IConfiguration configuration, ILogger<ReleaseWatch> logger) : BackgroundService
{
    public const string DefaultUrl = "https://api.github.com/repos/doodersrage/shelf/releases/latest";

    public bool Enabled => configuration.GetValue("Updates:Check", true);

    // The newest release seen, when it is newer than this server.
    public ShelfRelease? Newer { get; private set; }

    public DateTimeOffset? CheckedAt { get; private set; }

    public string? Problem { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            return;
        }

        // A moment after starting, so a restart is never held up by it.
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(12));
        do
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not look for a newer release; it will look again later.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var client = clients.CreateClient(nameof(ReleaseWatch));
            using var request = new HttpRequestMessage(HttpMethod.Get, configuration["Updates:Url"] is { Length: > 0 } url ? url : DefaultUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var answer = await client.SendAsync(request, cancellationToken);
            answer.EnsureSuccessStatusCode();
            Consider(await answer.Content.ReadAsStringAsync(cancellationToken), ShelfVersion.Number);
            (CheckedAt, Problem) = (DateTimeOffset.UtcNow, null);
            Heartbeats.Beat(nameof(ReleaseWatch));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            (CheckedAt, Problem) = (DateTimeOffset.UtcNow, ex.Message);
            Heartbeats.Beat(nameof(ReleaseWatch), ex.Message);
            logger.LogInformation("Could not ask whether a newer Shelf is out: {Problem}", ex.Message);
        }
    }

    // A release as GitHub describes it, taken when it is newer than the version running.
    public ShelfRelease? Consider(string json, string running)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean() || root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
        {
            return Newer;
        }

        var tag = root.TryGetProperty("tag_name", out var name) ? name.GetString() : null;
        var page = root.TryGetProperty("html_url", out var html) ? html.GetString() : null;
        DateTimeOffset? published = root.TryGetProperty("published_at", out var at) && at.ValueKind == JsonValueKind.String && at.TryGetDateTimeOffset(out var when) ? when : null;
        Newer = Parse(tag) is { } latest && Parse(running) is { } current && latest > current && page is not null && page.StartsWith("https://", StringComparison.Ordinal)
            ? new ShelfRelease(latest.ToString(3), page, published)
            : null;
        return Newer;
    }

    public static Version? Parse(string? text)
    {
        var plain = text?.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(plain, out var version) ? new Version(version.Major, version.Minor, Math.Max(0, version.Build)) : null;
    }

    // An admin who said "not now" to a release isn't told of it again; a newer one is told anew.
    public async Task<ShelfRelease?> ForAdminAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        if (Newer is not { } release)
        {
            return null;
        }

        var reader = await db.Readers.AsNoTracking().Where(item => item.Id == db.ReaderId && item.IsAdmin).Select(item => new { item.DismissedRelease }).FirstOrDefaultAsync(cancellationToken);
        return reader is null || reader.DismissedRelease == release.Version ? null : release;
    }

    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/admin/release/dismiss", async (HttpContext http, ShelfDb db, ReleaseWatch watch, CancellationToken cancellationToken) =>
        {
            if (watch.Newer is { } release)
            {
                await db.Readers.Where(item => item.Id == db.ReaderId && item.IsAdmin).ExecuteUpdateAsync(set => set.SetProperty(item => item.DismissedRelease, release.Version), cancellationToken);
            }

            var back = http.Request.Headers.Referer.ToString();
            return Results.LocalRedirect(Uri.TryCreate(back, UriKind.Absolute, out var from) && from.Host == http.Request.Host.Host ? from.PathAndQuery : "/");
        }).RequireAuthorization().ExcludeFromDescription();
}

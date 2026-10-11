using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Shelf.Api.Books;

public sealed class FetchProblem(string message) : Exception(message);

// What a download client says of one download.
public sealed record DownloadStatus(bool Found, bool Finished, bool Failed, double Progress, string? Path, string? Problem);

// Prowlarr: one search across every indexer it has, in the book and audiobook categories.
public static class ProwlarrClient
{
    public const string ClientName = "fetch";

    public static async Task<IReadOnlyList<Release>> SearchAsync(HttpClient http, string url, string apiKey, string query, FetchKind kind, CancellationToken cancellationToken)
    {
        var categories = kind == FetchKind.Ebook ? "&categories=7000&categories=7020&categories=7030" : "&categories=3030";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{url.TrimEnd('/')}/api/v1/search?query={Uri.EscapeDataString(query)}&type=search{categories}");
        request.Headers.Add("X-Api-Key", apiKey);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new FetchProblem(Localization.Words.T("Prowlarr did not take the API key."));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new FetchProblem(Localization.Words.T("Prowlarr answered {0}.", (int)response.StatusCode));
        }

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var releases = new List<Release>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var categories_ = item.TryGetProperty("categories", out var listed) && listed.ValueKind == JsonValueKind.Array
                ? listed.EnumerateArray().Select(category => category.TryGetProperty("id", out var id) && id.TryGetInt32(out var number) ? number : 0).Where(number => number > 0).ToArray()
                : [];
            releases.Add(new Release(
                Text(item, "guid") ?? Guid.NewGuid().ToString(),
                Text(item, "title") ?? "",
                item.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
                item.TryGetProperty("seeders", out var seeders) && seeders.TryGetInt32(out var seeding) ? seeding : null,
                (Text(item, "protocol") ?? "torrent").ToLowerInvariant(),
                Text(item, "downloadUrl"),
                Text(item, "magnetUrl"),
                Text(item, "indexer"),
                Text(item, "infoUrl"),
                categories_,
                DateTimeOffset.TryParse(Text(item, "publishDate"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var published) ? published : null));
        }

        return releases;
    }

    public static async Task<string?> TestAsync(HttpClient http, string url, string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{url.TrimEnd('/')}/api/v1/indexer");
            request.Headers.Add("X-Api-Key", apiKey);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return response.StatusCode == HttpStatusCode.Unauthorized ? Localization.Words.T("Prowlarr did not take the API key.") : Localization.Words.T("Prowlarr answered {0}.", (int)response.StatusCode);
            }

            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var count = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.GetArrayLength() : 0;
            return count == 0 ? Localization.Words.T("Prowlarr answered, but has no indexers yet.") : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Localization.Words.T("Prowlarr did not answer at that address.");
        }
    }

    // The release's file itself: a torrent, an NZB, or, when the indexer gives one, a magnet link.
    public static async Task<(byte[]? File, string? Magnet)> FetchAsync(HttpClient http, Release release, CancellationToken cancellationToken)
    {
        if (release.DownloadUrl is not { Length: > 0 } url)
        {
            return (null, release.MagnetUrl);
        }

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.Headers.Location is { } moved && moved.Scheme == "magnet")
        {
            return (null, moved.ToString());
        }

        if (!response.IsSuccessStatusCode)
        {
            if (release.MagnetUrl is { Length: > 0 } magnet)
            {
                return (null, magnet);
            }

            throw new FetchProblem(Localization.Words.T("The indexer would not give the file ({0}).", (int)response.StatusCode));
        }

        return (await response.Content.ReadAsByteArrayAsync(cancellationToken), null);
    }

    public static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
}

// qBittorrent's Web API: signs in for a cookie, adds the torrent under Shelf's category and a tag of its own, and
// asks after it by that tag.
public static class QbitClient
{
    private static async Task<string> SignInAsync(HttpClient http, string url, string? user, string? password, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync($"{url.TrimEnd('/')}/api/v2/auth/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = user ?? "", ["password"] = password ?? "" }), cancellationToken);
        var cookie = response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies.Select(item => item.Split(';')[0]).FirstOrDefault(item => item.StartsWith("SID=", StringComparison.Ordinal)) : null;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode || body.Trim() == "Fails." || cookie is null)
        {
            // Some set-ups let a local address in with no sign-in at all, and give no cookie: carry on without one.
            if (response.IsSuccessStatusCode && body.Trim() == "Ok.")
            {
                return "";
            }

            throw new FetchProblem(Localization.Words.T("qBittorrent did not take the user name and password."));
        }

        return cookie;
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string cookie, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        if (cookie.Length > 0)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return request;
    }

    public static string Tag(int grabId) => $"shelf-grab-{grabId}";

    public static async Task AddAsync(HttpClient http, string url, string? user, string? password, string category, int grabId, byte[]? torrent, string? magnet, CancellationToken cancellationToken)
    {
        var cookie = await SignInAsync(http, url, user, password, cancellationToken);
        var root = url.TrimEnd('/');
        using (var made = Request(HttpMethod.Post, $"{root}/api/v2/torrents/createCategory", cookie, new FormUrlEncodedContent(new Dictionary<string, string> { ["category"] = category })))
        using (await http.SendAsync(made, cancellationToken))
        {
            // There already, most times; that is fine.
        }

        using var form = new MultipartFormDataContent
        {
            { new StringContent(category), "category" },
            { new StringContent(Tag(grabId)), "tags" },
        };
        if (torrent is not null)
        {
            form.Add(new ByteArrayContent(torrent), "torrents", $"shelf-{grabId}.torrent");
        }
        else if (magnet is not null)
        {
            form.Add(new StringContent(magnet), "urls");
        }
        else
        {
            throw new FetchProblem(Localization.Words.T("The release has no file to download."));
        }

        using var add = Request(HttpMethod.Post, $"{root}/api/v2/torrents/add", cookie, form);
        using var response = await http.SendAsync(add, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode || body.Trim() == "Fails.")
        {
            throw new FetchProblem(Localization.Words.T("qBittorrent would not take the torrent."));
        }
    }

    public static async Task<DownloadStatus> StatusAsync(HttpClient http, string url, string? user, string? password, int grabId, CancellationToken cancellationToken)
    {
        var cookie = await SignInAsync(http, url, user, password, cancellationToken);
        using var request = Request(HttpMethod.Get, $"{url.TrimEnd('/')}/api/v2/torrents/info?tag={Uri.EscapeDataString(Tag(grabId))}", cookie);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var torrent = document.RootElement.EnumerateArray().FirstOrDefault();
        if (torrent.ValueKind != JsonValueKind.Object)
        {
            return new DownloadStatus(false, false, false, 0, null, null);
        }

        var progress = torrent.TryGetProperty("progress", out var given) && given.TryGetDouble(out var share) ? share : 0;
        var state = ProwlarrClient.Text(torrent, "state") ?? "";
        var path = ProwlarrClient.Text(torrent, "content_path") ?? ProwlarrClient.Text(torrent, "save_path");
        var failed = state is "error" or "missingFiles";
        return new DownloadStatus(true, progress >= 1 && !failed, failed, progress, path, failed ? Localization.Words.T("qBittorrent says the torrent has an error.") : null);
    }

    public static async Task<string?> TestAsync(HttpClient http, string url, string? user, string? password, CancellationToken cancellationToken)
    {
        try
        {
            var cookie = await SignInAsync(http, url, user, password, cancellationToken);
            using var request = Request(HttpMethod.Get, $"{url.TrimEnd('/')}/api/v2/app/version", cookie);
            using var response = await http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode ? null : Localization.Words.T("qBittorrent answered {0}.", (int)response.StatusCode);
        }
        catch (FetchProblem problem)
        {
            return problem.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Localization.Words.T("qBittorrent did not answer at that address.");
        }
    }
}

// SABnzbd's API: adds the NZB under Shelf's category, named for the grab, and finds it in the queue or the history.
public static class SabClient
{
    public static string Name(int grabId) => $"shelf-grab-{grabId}";

    private static string Api(string url, string apiKey, string mode) => $"{url.TrimEnd('/')}/api?mode={mode}&apikey={Uri.EscapeDataString(apiKey)}&output=json";

    public static async Task<string> AddAsync(HttpClient http, string url, string apiKey, string category, int grabId, byte[] nzb, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(nzb), "name", $"{Name(grabId)}.nzb" } };
        using var response = await http.PostAsync($"{Api(url, apiKey, "addfile")}&cat={Uri.EscapeDataString(category)}&nzbname={Name(grabId)}", form, cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (document.RootElement.TryGetProperty("nzo_ids", out var ids) && ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() > 0 && ids[0].GetString() is { } id)
        {
            return id;
        }

        throw new FetchProblem(ProwlarrClient.Text(document.RootElement, "error") ?? Localization.Words.T("SABnzbd would not take the NZB."));
    }

    public static async Task<DownloadStatus> StatusAsync(HttpClient http, string url, string apiKey, string downloadId, CancellationToken cancellationToken)
    {
        using (var queue = await http.GetAsync($"{Api(url, apiKey, "queue")}&nzo_ids={Uri.EscapeDataString(downloadId)}", cancellationToken))
        {
            using var document = await JsonDocument.ParseAsync(await queue.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("queue", out var list) && list.TryGetProperty("slots", out var slots))
            {
                foreach (var slot in slots.EnumerateArray().Where(slot => ProwlarrClient.Text(slot, "nzo_id") == downloadId))
                {
                    var percent = double.TryParse(ProwlarrClient.Text(slot, "percentage"), CultureInfo.InvariantCulture, out var share) ? share / 100 : 0;
                    return new DownloadStatus(true, false, false, percent, null, null);
                }
            }
        }

        using var history = await http.GetAsync($"{Api(url, apiKey, "history")}&nzo_ids={Uri.EscapeDataString(downloadId)}", cancellationToken);
        using var past = await JsonDocument.ParseAsync(await history.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (past.RootElement.TryGetProperty("history", out var record) && record.TryGetProperty("slots", out var done))
        {
            foreach (var slot in done.EnumerateArray().Where(slot => ProwlarrClient.Text(slot, "nzo_id") == downloadId))
            {
                var status = ProwlarrClient.Text(slot, "status") ?? "";
                return status switch
                {
                    "Completed" => new DownloadStatus(true, true, false, 1, ProwlarrClient.Text(slot, "storage"), null),
                    "Failed" => new DownloadStatus(true, false, true, 1, null, ProwlarrClient.Text(slot, "fail_message") ?? Localization.Words.T("SABnzbd could not finish it.")),
                    // Verifying, repairing, unpacking: nearly there.
                    _ => new DownloadStatus(true, false, false, 0.99, null, null),
                };
            }
        }

        return new DownloadStatus(false, false, false, 0, null, null);
    }

    public static async Task<string?> TestAsync(HttpClient http, string url, string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync($"{Api(url, apiKey, "queue")}&limit=1", cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return response.IsSuccessStatusCode && body.Contains("\"queue\"", StringComparison.Ordinal) ? null
                : body.Contains("API Key", StringComparison.OrdinalIgnoreCase) ? Localization.Words.T("SABnzbd did not take the API key.")
                : Localization.Words.T("SABnzbd answered {0}.", (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Localization.Words.T("SABnzbd did not answer at that address.");
        }
    }
}

// Deluge's web interface, by its JSON-RPC: signs in with the web password, makes sure it is connected to a daemon,
// adds the torrent (into the Label plugin's label for Shelf, where the plugin is on), and asks after it by its hash.
public static class DelugeClient
{
    private static int next;

    private static async Task<(JsonElement Result, string? Cookie)> CallAsync(HttpClient http, string url, string? cookie, string method, object?[] parameters, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{url.TrimEnd('/')}/json")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { method, @params = parameters, id = Interlocked.Increment(ref next) }),
        };
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new FetchProblem(Localization.Words.T("Deluge answered {0}.", (int)response.StatusCode));
        }

        var set = response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies.Select(item => item.Split(';')[0]).FirstOrDefault(item => item.StartsWith("_session_id=", StringComparison.Ordinal)) : null;
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            throw new FetchProblem(ProwlarrClient.Text(error, "message") ?? Localization.Words.T("Deluge refused the request."));
        }

        return (document.RootElement.GetProperty("result").Clone(), set ?? cookie);
    }

    // Signed in and connected to a daemon, the first one the web interface knows, if it is not connected already.
    private static async Task<string> SessionAsync(HttpClient http, string url, string? password, CancellationToken cancellationToken)
    {
        var (signedIn, cookie) = await CallAsync(http, url, null, "auth.login", [password ?? ""], cancellationToken);
        if (signedIn.ValueKind != JsonValueKind.True || cookie is null)
        {
            throw new FetchProblem(Localization.Words.T("Deluge did not take the password."));
        }

        var (connected, _) = await CallAsync(http, url, cookie, "web.connected", [], cancellationToken);
        if (connected.ValueKind != JsonValueKind.True)
        {
            var (hosts, _) = await CallAsync(http, url, cookie, "web.get_hosts", [], cancellationToken);
            var host = hosts.ValueKind == JsonValueKind.Array ? hosts.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Array && item.GetArrayLength() > 0 ? item[0].GetString() : null).FirstOrDefault(id => id is not null) : null;
            if (host is null)
            {
                throw new FetchProblem(Localization.Words.T("Deluge's web interface is not connected to a Deluge daemon."));
            }

            await CallAsync(http, url, cookie, "web.connect", [host], cancellationToken);
        }

        return cookie;
    }

    // Returns the torrent's hash, by which Deluge knows it from then on.
    public static async Task<string> AddAsync(HttpClient http, string url, string? password, string category, int grabId, byte[]? torrent, string? magnet, CancellationToken cancellationToken)
    {
        var cookie = await SessionAsync(http, url, password, cancellationToken);
        var options = new Dictionary<string, object>();
        var (added, _) = torrent is not null
            ? await CallAsync(http, url, cookie, "core.add_torrent_file", [$"shelf-{grabId}.torrent", Convert.ToBase64String(torrent), options], cancellationToken)
            : magnet is not null
                ? await CallAsync(http, url, cookie, "core.add_torrent_magnet", [magnet, options], cancellationToken)
                : throw new FetchProblem(Localization.Words.T("The release has no file to download."));
        if (added.ValueKind != JsonValueKind.String || added.GetString() is not { Length: > 0 } hash)
        {
            throw new FetchProblem(Localization.Words.T("Deluge would not take the torrent; it may have it already."));
        }

        // The Label plugin, where it is turned on, files it under Shelf's category; without it, nothing is lost.
        try
        {
            try
            {
                await CallAsync(http, url, cookie, "label.add", [category.ToLowerInvariant()], cancellationToken);
            }
            catch (FetchProblem)
            {
                // The label is there already.
            }

            await CallAsync(http, url, cookie, "label.set_torrent", [hash, category.ToLowerInvariant()], cancellationToken);
        }
        catch (FetchProblem)
        {
            // No Label plugin.
        }

        return hash;
    }

    public static async Task<DownloadStatus> StatusAsync(HttpClient http, string url, string? password, string hash, CancellationToken cancellationToken)
    {
        var cookie = await SessionAsync(http, url, password, cancellationToken);
        var (status, _) = await CallAsync(http, url, cookie, "core.get_torrent_status", [hash, new[] { "progress", "state", "save_path", "name", "is_finished", "message" }], cancellationToken);
        if (status.ValueKind != JsonValueKind.Object || !status.EnumerateObject().Any())
        {
            return new DownloadStatus(false, false, false, 0, null, null);
        }

        var progress = status.TryGetProperty("progress", out var given) && given.TryGetDouble(out var percent) ? percent / 100 : 0;
        var state = ProwlarrClient.Text(status, "state") ?? "";
        var finished = status.TryGetProperty("is_finished", out var done) && done.ValueKind == JsonValueKind.True;
        var path = ProwlarrClient.Text(status, "save_path") is { } folder && ProwlarrClient.Text(status, "name") is { } name ? $"{folder.TrimEnd('/', '\\')}/{name}" : null;
        var failed = state == "Error";
        return new DownloadStatus(true, (finished || progress >= 1) && !failed, failed, progress, path, failed ? ProwlarrClient.Text(status, "message") ?? Localization.Words.T("Deluge says the torrent has an error.") : null);
    }

    public static async Task<string?> TestAsync(HttpClient http, string url, string? password, CancellationToken cancellationToken)
    {
        try
        {
            await SessionAsync(http, url, password, cancellationToken);
            return null;
        }
        catch (FetchProblem problem)
        {
            return problem.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return Localization.Words.T("Deluge did not answer at that address.");
        }
    }
}

// Jackett: one search across every indexer it has set up, by its JSON results, in the book and audiobook categories.
public static class JackettClient
{
    public static async Task<IReadOnlyList<Release>> SearchAsync(HttpClient http, string url, string apiKey, string query, FetchKind kind, CancellationToken cancellationToken)
    {
        var categories = kind == FetchKind.Ebook ? "&Category%5B%5D=7000&Category%5B%5D=7020&Category%5B%5D=7030" : "&Category%5B%5D=3030";
        using var response = await http.GetAsync($"{url.TrimEnd('/')}/api/v2.0/indexers/all/results?apikey={Uri.EscapeDataString(apiKey)}&Query={Uri.EscapeDataString(query)}{categories}", cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new FetchProblem(Localization.Words.T("Jackett did not take the API key."));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new FetchProblem(Localization.Words.T("Jackett answered {0}.", (int)response.StatusCode));
        }

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var releases = new List<Release>();
        if (!document.RootElement.TryGetProperty("Results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return releases;
        }

        foreach (var item in results.EnumerateArray())
        {
            var categories_ = item.TryGetProperty("Category", out var listed) && listed.ValueKind == JsonValueKind.Array
                ? listed.EnumerateArray().Select(category => category.TryGetInt32(out var number) ? number : 0).Where(number => number > 0).ToArray()
                : [];
            releases.Add(new Release(
                ProwlarrClient.Text(item, "Guid") ?? ProwlarrClient.Text(item, "Link") ?? Guid.NewGuid().ToString(),
                ProwlarrClient.Text(item, "Title") ?? "",
                item.TryGetProperty("Size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
                item.TryGetProperty("Seeders", out var seeders) && seeders.TryGetInt32(out var seeding) ? seeding : null,
                "torrent",
                ProwlarrClient.Text(item, "Link"),
                ProwlarrClient.Text(item, "MagnetUri"),
                ProwlarrClient.Text(item, "Tracker"),
                ProwlarrClient.Text(item, "Details"),
                categories_,
                DateTimeOffset.TryParse(ProwlarrClient.Text(item, "PublishDate"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var published) ? published : null));
        }

        return releases;
    }

    // Its Torznab capabilities, which need the key: an answer means the address and the key are both right.
    public static async Task<string?> TestAsync(HttpClient http, string url, string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync($"{url.TrimEnd('/')}/api/v2.0/indexers/all/results/torznab/api?apikey={Uri.EscapeDataString(apiKey)}&t=caps", cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode && body.Contains("<caps", StringComparison.Ordinal))
            {
                return null;
            }

            return body.Contains("API Key", StringComparison.OrdinalIgnoreCase) || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? Localization.Words.T("Jackett did not take the API key.")
                : Localization.Words.T("Jackett answered {0}.", (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Localization.Words.T("Jackett did not answer at that address.");
        }
    }
}

// Whichever indexer service the shelf is set to.
public static class Indexers
{
    public static Task<IReadOnlyList<Release>> SearchAsync(HttpClient http, FetchSetting settings, string apiKey, string query, FetchKind kind, CancellationToken cancellationToken) =>
        settings.IndexerKind == IndexerKind.Prowlarr
            ? ProwlarrClient.SearchAsync(http, settings.IndexerUrl!, apiKey, query, kind, cancellationToken)
            : JackettClient.SearchAsync(http, settings.IndexerUrl!, apiKey, query, kind, cancellationToken);

    public static Task<string?> TestAsync(HttpClient http, IndexerKind kind, string url, string apiKey, CancellationToken cancellationToken) =>
        kind == IndexerKind.Prowlarr ? ProwlarrClient.TestAsync(http, url, apiKey, cancellationToken) : JackettClient.TestAsync(http, url, apiKey, cancellationToken);
}

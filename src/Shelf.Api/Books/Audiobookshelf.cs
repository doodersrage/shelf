using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Localization;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public sealed record AbsLibrary(string Id, string Name);

// A book in an Audiobookshelf library, as its list describes it.
public sealed record AbsItem(string Id, string Title, string Author, int AudioFiles, string? EbookFormat, double Duration);

// Admin: the Audiobookshelf user is one of its admins (admin or root), who may read everyone's progress.
public sealed record AbsConnection(Uri Server, string Token, string User, bool CanDownload, IReadOnlyList<AbsLibrary> Libraries, bool Admin = false, string? UserId = null);

// What comes across besides the books: where the reader stopped (with when they started and finished), their
// bookmarks, their listening history for Insights, and their collections and playlists as collections. With
// UpdateExisting, all of it is brought for books already on the shelf too, without their files coming again.
public sealed record AbsOptions(bool Progress = true, bool Bookmarks = true, bool History = true, bool Collections = true, bool UpdateExisting = true)
{
    public static readonly AbsOptions Nothing = new(false, false, false, false, false);

    // For keeping in step, which once only brought where the reader stopped.
    public static AbsOptions Place(bool bring) => bring ? new(true, true, true, false, false) : Nothing;

    public bool Any => Progress || Bookmarks || History || Collections;
}

// An Audiobookshelf user, as one of its admins sees them.
public sealed record AbsUser(string Id, string Name, string? Email, bool Admin);

// One reader's place in one book in Audiobookshelf. Times are milliseconds since 1970, as it keeps them.
public sealed record AbsProgress(double CurrentTime, bool Finished, long? StartedAt, long? FinishedAt, string? EbookLocation, double? EbookProgress);

public sealed record AbsBookmark(double Time, string? Title, long? CreatedAt);

public sealed record AbsList(string Name, IReadOnlyList<string> ItemIds);

// Everything one reader did in Audiobookshelf, by library item: progress, bookmarks, seconds listened each day, and
// the lists they keep.
public sealed record AbsReadingLife(
    IReadOnlyDictionary<string, AbsProgress> Progress,
    IReadOnlyDictionary<string, List<AbsBookmark>> Bookmarks,
    IReadOnlyDictionary<string, Dictionary<DateOnly, int>> History,
    IReadOnlyList<AbsList> Lists);

public sealed class AbsProblem(string message) : Exception(message);

// Talking to an Audiobookshelf server (2.x) with a user's API key or access token, sent as a Bearer token.
public static partial class AbsClient
{
    public const string ClientName = "audiobookshelf";

    // For a reader who isn't an admin: their Audiobookshelf must be on the internet, unless an admin lets readers'
    // addresses reach the local network (Network:AllowPrivateAddresses), so the shelf can't be pointed inside it.
    public const string ReaderClientName = "audiobookshelf-reader";

    public static string ClientFor(bool admin) => admin ? ClientName : ReaderClientName;

    public static Uri? ServerAddress(string? typed)
    {
        var text = typed?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }

        return Uri.TryCreate(text + "/", UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;
    }

    // An API key from its settings, or a user name and password traded for an access token.
    public static async Task<AbsConnection> ConnectAsync(HttpClient http, Uri server, string? apiKey, string? user, string? password, CancellationToken cancellationToken)
    {
        string token;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            token = apiKey.Trim();
        }
        else
        {
            using var login = await Send(http, HttpMethod.Post, new Uri(server, "login"), null, JsonContent.Create(new { username = user ?? "", password = password ?? "" }), cancellationToken);
            if (login.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new AbsProblem(T("Audiobookshelf did not accept that user name and password."));
            }

            await Ensure(login);
            using var answer = await JsonDocument.ParseAsync(await login.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            token = Text(answer.RootElement.GetProperty("user"), "accessToken") ?? Text(answer.RootElement.GetProperty("user"), "token")
                ?? throw new AbsProblem(T("Audiobookshelf signed in but sent no token back."));
        }

        using var authorize = await Send(http, HttpMethod.Post, new Uri(server, "api/authorize"), token, null, cancellationToken);
        if (authorize.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new AbsProblem(T("Audiobookshelf did not accept that API key. Check that it is turned on (active) in Settings, API Keys."));
        }

        await Ensure(authorize);
        using var me = await JsonDocument.ParseAsync(await authorize.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var account = me.RootElement.GetProperty("user");
        var canDownload = account.TryGetProperty("permissions", out var permissions)
            && permissions.TryGetProperty("download", out var download) && download.ValueKind == JsonValueKind.True;
        var admin = Text(account, "type") is "admin" or "root";

        using var listed = await Send(http, HttpMethod.Get, new Uri(server, "api/libraries"), token, null, cancellationToken);
        await Ensure(listed);
        using var libraries = await JsonDocument.ParseAsync(await listed.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var books = libraries.RootElement.GetProperty("libraries").EnumerateArray()
            .Where(library => Text(library, "mediaType") == "book")
            .Select(library => new AbsLibrary(Text(library, "id")!, Text(library, "name") ?? "Library"))
            .ToList();
        return new AbsConnection(server, token, Text(account, "username") ?? "", canDownload, books, admin, Text(account, "id"));
    }

    // Every user on the server, for one of its admins.
    public static async Task<List<AbsUser>> UsersAsync(HttpClient http, AbsConnection connection, CancellationToken cancellationToken)
    {
        using var response = await Send(http, HttpMethod.Get, new Uri(connection.Server, "api/users"), connection.Token, null, cancellationToken);
        await Ensure(response);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return [.. document.RootElement.GetProperty("users").EnumerateArray()
            .Where(user => !(user.TryGetProperty("isActive", out var active) && active.ValueKind == JsonValueKind.False))
            .Select(user => new AbsUser(Text(user, "id")!, Text(user, "username") ?? "?", Text(user, "email"), Text(user, "type") is "admin" or "root"))
            .OrderBy(user => user.Name, StringComparer.OrdinalIgnoreCase)];
    }

    // One reader's progress, bookmarks, listening history, and lists: the connected user's own, or, for one of the
    // server's admins, any user's. Whatever an older Audiobookshelf does not answer is left out rather than failing.
    public static async Task<AbsReadingLife> ReadingLifeAsync(HttpClient http, AbsConnection connection, string? userId, AbsOptions options, CancellationToken cancellationToken)
    {
        var progress = new Dictionary<string, AbsProgress>(StringComparer.Ordinal);
        var bookmarks = new Dictionary<string, List<AbsBookmark>>(StringComparer.Ordinal);
        var history = new Dictionary<string, Dictionary<DateOnly, int>>(StringComparer.Ordinal);
        var lists = new List<AbsList>();
        var who = userId is null ? "api/me" : $"api/users/{Uri.EscapeDataString(userId)}";

        if (options.Progress || options.Bookmarks || options.UpdateExisting)
        {
            using var document = await JsonAsync(http, connection, who, cancellationToken);
            var user = document?.RootElement.TryGetProperty("user", out var wrapped) == true ? wrapped : document?.RootElement;
            if (user is { } found && found.TryGetProperty("mediaProgress", out var all) && all.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in all.EnumerateArray().Where(entry => Text(entry, "episodeId") is null && Text(entry, "libraryItemId") is not null))
                {
                    progress[Text(entry, "libraryItemId")!] = new AbsProgress(
                        Number(entry, "currentTime") ?? 0,
                        entry.TryGetProperty("isFinished", out var finished) && finished.ValueKind == JsonValueKind.True,
                        Whole(entry, "startedAt"),
                        Whole(entry, "finishedAt"),
                        Text(entry, "ebookLocation"),
                        Number(entry, "ebookProgress"));
                }
            }

            if (user is { } owner && owner.TryGetProperty("bookmarks", out var marks) && marks.ValueKind == JsonValueKind.Array)
            {
                foreach (var mark in marks.EnumerateArray().Where(mark => Text(mark, "libraryItemId") is not null && Number(mark, "time") is not null))
                {
                    var list = bookmarks.TryGetValue(Text(mark, "libraryItemId")!, out var kept) ? kept : bookmarks[Text(mark, "libraryItemId")!] = [];
                    list.Add(new AbsBookmark(Number(mark, "time")!.Value, Text(mark, "title"), Whole(mark, "createdAt")));
                }
            }
        }

        if (options.History)
        {
            for (var page = 0; page < 400; page++)
            {
                using var document = await JsonAsync(http, connection, $"{who}/listening-sessions?itemsPerPage=500&page={page}", cancellationToken);
                if (document is null || !document.RootElement.TryGetProperty("sessions", out var sessions) || sessions.ValueKind != JsonValueKind.Array || sessions.GetArrayLength() == 0)
                {
                    break;
                }

                foreach (var session in sessions.EnumerateArray())
                {
                    if (Text(session, "libraryItemId") is { } item && Text(session, "episodeId") is null
                        && DateOnly.TryParseExact(Text(session, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                        && Number(session, "timeListening") is double seconds && seconds >= 1)
                    {
                        var days = history.TryGetValue(item, out var kept) ? kept : history[item] = [];
                        days[day] = days.GetValueOrDefault(day) + (int)Math.Min(seconds, 86400);
                    }
                }

                if (Number(document.RootElement, "numPages") is double pages && page + 1 >= pages)
                {
                    break;
                }
            }
        }

        if (options.Collections)
        {
            // Collections belong to the library, so every user sees the same ones; playlists are the reader's own.
            using (var document = await JsonAsync(http, connection, "api/collections", cancellationToken))
            {
                if (document?.RootElement.TryGetProperty("collections", out var collections) == true && collections.ValueKind == JsonValueKind.Array)
                {
                    lists.AddRange(collections.EnumerateArray().Select(collection => new AbsList(Text(collection, "name") ?? "Audiobookshelf",
                        collection.TryGetProperty("books", out var books) && books.ValueKind == JsonValueKind.Array
                            ? [.. books.EnumerateArray().Select(book => Text(book, "id")).OfType<string>()]
                            : [])));
                }
            }

            if (userId is null)
            {
                using var document = await JsonAsync(http, connection, "api/playlists", cancellationToken);
                if (document?.RootElement.TryGetProperty("playlists", out var playlists) == true && playlists.ValueKind == JsonValueKind.Array)
                {
                    lists.AddRange(playlists.EnumerateArray().Select(playlist => new AbsList(Text(playlist, "name") ?? "Audiobookshelf",
                        playlist.TryGetProperty("items", out var entries) && entries.ValueKind == JsonValueKind.Array
                            ? [.. entries.EnumerateArray().Where(entry => Text(entry, "episodeId") is null).Select(entry => Text(entry, "libraryItemId")).OfType<string>()]
                            : [])));
                }
            }
        }

        return new AbsReadingLife(progress, bookmarks, history, lists);
    }

    // An answer as JSON, or null when Audiobookshelf has no such thing or will not say.
    private static async Task<JsonDocument?> JsonAsync(HttpClient http, AbsConnection connection, string path, CancellationToken cancellationToken)
    {
        using var response = await Send(http, HttpMethod.Get, new Uri(connection.Server, path), connection.Token, null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static long? Whole(JsonElement element, string name) => Number(element, name) is double number && number > 0 ? (long)number : null;

    // Every book in a library, five hundred at a time in the short form, saying how far it has got as it goes.
    public static async Task<List<AbsItem>> ItemsAsync(HttpClient http, AbsConnection connection, string libraryId, CancellationToken cancellationToken, IProgress<(int Listed, int Total)>? progress = null)
    {
        var items = new List<AbsItem>();
        for (var page = 0; page < 1000; page++)
        {
            var url = new Uri(connection.Server, $"api/libraries/{Uri.EscapeDataString(libraryId)}/items?limit=500&page={page}&minified=1&sort=media.metadata.title");
            using var response = await Send(http, HttpMethod.Get, url, connection.Token, null, cancellationToken);
            await Ensure(response);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var results = document.RootElement.GetProperty("results");
            foreach (var item in results.EnumerateArray())
            {
                var media = item.GetProperty("media");
                var metadata = media.GetProperty("metadata");
                items.Add(new AbsItem(
                    Text(item, "id")!,
                    Text(metadata, "title") ?? "Untitled",
                    Text(metadata, "authorName") is { Length: > 0 } author ? author : BookImport.UnknownAuthor,
                    media.TryGetProperty("numAudioFiles", out var count) && count.ValueKind == JsonValueKind.Number ? count.GetInt32() : 0,
                    Text(media, "ebookFormat"),
                    media.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.Number ? duration.GetDouble() : 0));
            }

            var total = document.RootElement.TryGetProperty("total", out var all) && all.ValueKind == JsonValueKind.Number ? all.GetInt32() : items.Count;
            progress?.Report((items.Count, total));
            if (results.GetArrayLength() == 0 || items.Count >= total)
            {
                break;
            }
        }

        return items;
    }

    public static async Task<HttpResponseMessage> Send(HttpClient http, HttpMethod method, Uri url, string? token, HttpContent? content, CancellationToken cancellationToken, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        request.Headers.Accept.ParseAdd("application/json");
        try
        {
            return await http.SendAsync(request, completion, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or Polly.ExecutionRejectedException)
        {
            throw new AbsProblem(T("Shelf could not reach {0}. Check the address, and that this server can reach it.", url.GetLeftPart(UriPartial.Authority)));
        }
    }

    private static async Task Ensure(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.NotFound && response.RequestMessage?.RequestUri?.AbsolutePath.EndsWith("/api/libraries", StringComparison.Ordinal) == true)
        {
            throw new AbsProblem(T("That address does not answer as Audiobookshelf. If it is served under a path, include it, such as https://example.org/audiobookshelf."));
        }

        if (!response.IsSuccessStatusCode)
        {
            var reason = response.Content is null ? "" : (await response.Content.ReadAsStringAsync()).Trim();
            throw new AbsProblem(reason.Length is > 0 and < 200
                ? T("Audiobookshelf answered {0}: {1}", (int)response.StatusCode, reason)
                : T("Audiobookshelf answered {0}.", (int)response.StatusCode));
        }
    }

    public static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text.Trim()
            : null;

    // An EPUB CFI's first steps name the spine item: /6/8! is the fourth.
    public static int? SpineIndex(string? cfi)
    {
        var match = Cfi().Match(cfi ?? "");
        return match.Success && int.TryParse(match.Groups[1].Value, out var step) && step >= 2 ? step / 2 - 1 : null;
    }

    [GeneratedRegex(@"epubcfi\(/6/(\d+)")]
    private static partial Regex Cfi();
}

public enum AbsState
{
    Waiting,
    Downloading,
    Done,
    Skipped,
    Failed,
}

public sealed record AbsImportLine(string ItemId, string Title, AbsState State, long Bytes, int? BookId, string? Note);

// An import for one reader. AbsUserId names whose reading comes with it, for an admin bringing everyone's (null: the
// connected user's own); StartedOnly narrows the books to those that reader started or finished, once that is known.
public sealed class AbsImportJob(
    Guid id,
    int readerId,
    AbsConnection connection,
    AbsOptions options,
    IReadOnlyList<AbsItem> items,
    string clientName = AbsClient.ReaderClientName,
    string? absUserId = null,
    string? forName = null,
    bool startedOnly = false) : IShelfJob
{
    public string ClientName { get; } = clientName;
    public AbsOptions Options { get; } = options;
    public string? AbsUserId { get; } = absUserId;
    public string? ForName { get; } = forName;
    public bool StartedOnly { get; } = startedOnly;
    public AbsReadingLife? Life { get; set; }
    public int ListsMade { get; set; }

    public string Language { get; } = Words.Current;

    public Guid Id { get; } = id;
    public int ReaderId { get; } = readerId;
    public AbsConnection Connection { get; } = connection;
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
    public ConcurrentDictionary<string, AbsImportLine> Books { get; private set; } = Lined(items);
    public IReadOnlyList<AbsItem> Items { get; private set; } = items;
    public bool Finished { get; set; }
    public bool Stopping { get; private set; }

    private static ConcurrentDictionary<string, AbsImportLine> Lined(IReadOnlyList<AbsItem> items) =>
        new(items.Select(item => KeyValuePair.Create(item.Id, new AbsImportLine(item.Id, item.Title, AbsState.Waiting, 0, null, null))));

    // Only these books after all: those the reader started or finished.
    public void Narrow(IReadOnlyList<AbsItem> items) => (Items, Books) = (items, Lined(items));

    public string Title(bool finished) => (finished, ForName) switch
    {
        (true, null) => T("Brought across from {0}", "Audiobookshelf"),
        (false, null) => T("Bringing books from {0}", "Audiobookshelf"),
        (true, { } name) => T("Brought across from Audiobookshelf for {0}", name),
        (false, { } name) => T("Bringing books from Audiobookshelf for {0}", name),
    };

    public string Page => "/import/audiobookshelf";

    public void Stop() => Stopping = true;

    public IReadOnlyList<ImportLine> Lines() => [.. Items.Select(item => Books[item.Id]).Select(line => new ImportLine(line.Title, line.State switch
    {
        AbsState.Waiting => ImportLineState.Waiting,
        AbsState.Downloading => ImportLineState.Working,
        AbsState.Done => ImportLineState.Done,
        AbsState.Skipped => ImportLineState.Skipped,
        _ => ImportLineState.Failed,
    }, line.Note, line.BookId, line.Bytes))];
}

// Imports run one at a time in the background, each book in turn: its tracks in order, its e-book, its cover, and,
// when asked, where the reader stopped. The key or token is held only until the import ends.
public sealed class AbsImporter(IServiceScopeFactory scopes, IHttpClientFactory clients, ShelfJobs register, ILogger<AbsImporter> logger) : BackgroundService
{
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, AbsImportJob> _jobs = new();

    public AbsImportJob Start(int readerId, AbsConnection connection, AbsOptions options, IReadOnlyList<AbsItem> items, bool admin = false, string? absUserId = null, string? forName = null, bool startedOnly = false)
    {
        var job = new AbsImportJob(Guid.NewGuid(), readerId, connection, options, items, AbsClient.ClientFor(admin), absUserId, forName, startedOnly);
        _jobs[job.Id] = job;
        register.Add(job);
        _queue.Writer.TryWrite(job.Id);
        return job;
    }

    public AbsImportJob? Latest(int readerId) =>
        _jobs.Values.Where(job => job.ReaderId == readerId).OrderByDescending(job => job.Started).FirstOrDefault();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (!_jobs.TryGetValue(id, out var job))
            {
                continue;
            }

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(job.Language);
            try
            {
                await RunAsync(job, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Something no one foresaw ends this import, not the importer: the next one still runs.
                logger.LogError(ex, "An Audiobookshelf import stopped unexpectedly.");
                foreach (var item in job.Items.Where(item => job.Books[item.Id].State is AbsState.Waiting or AbsState.Downloading))
                {
                    Set(job, item.Id, line => line with { State = AbsState.Failed, Note = T("The import stopped unexpectedly. Try it again.") });
                }

                job.Finished = true;
            }
        }
    }

    private async Task RunAsync(AbsImportJob job, CancellationToken stoppingToken)
    {
        // The reader's own progress, bookmarks, history, and lists, read once for the whole import.
        if (job.Options.Any || job.StartedOnly)
        {
            try
            {
                job.Life = await AbsClient.ReadingLifeAsync(clients.CreateClient(job.ClientName), job.Connection, job.AbsUserId,
                    job.Options with { Progress = job.Options.Progress || job.StartedOnly }, stoppingToken);
            }
            catch (Exception ex) when (ex is AbsProblem or HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                logger.LogInformation(ex, "Could not read the reader's progress from Audiobookshelf; the books come without it.");
            }
        }

        if (job.StartedOnly)
        {
            job.Narrow([.. job.Items.Where(item => job.Life?.Progress.ContainsKey(item.Id) == true || job.Life?.History.ContainsKey(item.Id) == true)]);
        }

        var onShelf = await OnShelfAsync(job, stoppingToken);
        foreach (var item in job.Items)
        {
            if (job.Stopping)
            {
                Set(job, item.Id, line => line with { State = AbsState.Skipped, Note = T("Stopped before this one.") });
                continue;
            }

            try
            {
                if (onShelf.TryGetValue(item.Id, out var bookId))
                {
                    await UpdateAsync(job, item, bookId, stoppingToken);
                }
                else
                {
                    await ImportAsync(job, item, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is AbsProblem or HttpRequestException or Polly.ExecutionRejectedException or IOException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                logger.LogInformation(ex, "Could not import {Title} from Audiobookshelf.", item.Title);
                Set(job, item.Id, line => line with { State = AbsState.Failed, Note = ex is AbsProblem ? ex.Message : T("The download did not finish.") });
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // One book that fails in a way no one foresaw fails alone; the rest still come.
                logger.LogWarning(ex, "Could not import {Title} from Audiobookshelf.", item.Title);
                Set(job, item.Id, line => line with { State = AbsState.Failed, Note = T("Something went wrong with this one. Try it again.") });
            }
        }

        try
        {
            await ListsAsync(job, stoppingToken);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            logger.LogInformation(ex, "Could not make the collections from Audiobookshelf.");
        }

        job.Finished = true;
        await NoteAsync(job, stoppingToken);
    }

    private static void Set(AbsImportJob job, string itemId, Func<AbsImportLine, AbsImportLine> change) =>
        job.Books.AddOrUpdate(itemId, _ => throw new KeyNotFoundException(), (_, line) => change(line));

    private async Task ImportAsync(AbsImportJob job, AbsItem item, CancellationToken cancellationToken)
    {
        var http = clients.CreateClient(job.ClientName);
        var server = job.Connection.Server;
        var token = job.Connection.Token;
        Set(job, item.Id, line => line with { State = AbsState.Downloading });

        using var found = await AbsClient.Send(http, HttpMethod.Get, new Uri(server, $"api/items/{Uri.EscapeDataString(item.Id)}?expanded=1"), token, null, cancellationToken);
        if (!found.IsSuccessStatusCode)
        {
            throw new AbsProblem(T("Audiobookshelf answered {0} for this book.", (int)found.StatusCode));
        }

        using var document = await JsonDocument.ParseAsync(await found.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var media = document.RootElement.GetProperty("media");
        var metadata = media.GetProperty("metadata");
        var known = new FileDetails(
            AbsClient.Text(metadata, "title") ?? item.Title,
            AbsClient.Text(metadata, "authorName") ?? item.Author,
            AbsClient.Text(metadata, "isbn"),
            AbsClient.Text(metadata, "publisher"),
            int.TryParse(AbsClient.Text(metadata, "publishedYear"), out var year) ? year : null,
            AbsClient.Text(metadata, "language"));
        var audioFiles = media.TryGetProperty("audioFiles", out var files) && files.ValueKind == JsonValueKind.Array
            ? files.EnumerateArray()
                .Where(file => !(file.TryGetProperty("exclude", out var excluded) && excluded.ValueKind == JsonValueKind.True))
                .OrderBy(file => file.TryGetProperty("index", out var index) && index.ValueKind == JsonValueKind.Number ? index.GetInt32() : 0)
                .ToList()
            : [];
        var ebookFile = media.TryGetProperty("ebookFile", out var ebook) && ebook.ValueKind == JsonValueKind.Object ? ebook : (JsonElement?)null;

        var folder = Path.Combine(Path.GetTempPath(), $"shelf-abs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(job.ReaderId);
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var import = scope.ServiceProvider.GetRequiredService<BookImport>();
            var covers = scope.ServiceProvider.GetRequiredService<CoverStore>();
            ImportedFile? heard = null;
            ImportedFile? read = null;
            long total = 0;

            // Only as many tracks as a book holds are fetched; the rest are named in the line, not dropped unsaid.
            var tracksLeft = Math.Max(0, audioFiles.Count - AudioStore.MaxTracks);
            if (audioFiles.Count > 0)
            {
                var downloaded = new List<(string Path, string Name)>();
                foreach (var (file, position) in audioFiles.Take(AudioStore.MaxTracks).Select((file, position) => (file, position)))
                {
                    var name = Path.GetFileName(AbsClient.Text(file.GetProperty("metadata"), "filename") ?? $"track{Path.GetExtension(AbsClient.Text(file.GetProperty("metadata"), "ext") ?? ".mp3")}");
                    // Numbered on disk, since two discs can each have a 01.mp3; the order Audiobookshelf gives is kept.
                    var path = Path.Combine(folder, $"{position:0000}{Path.GetExtension(name)}");
                    total = await DownloadAsync(http, new Uri(server, $"api/items/{Uri.EscapeDataString(item.Id)}/file/{AbsClient.Text(file, "ino")}/download"), token, path, total, AudioStore.MaxBytes, job, item.Id, cancellationToken);
                    downloaded.Add((path, name));
                }

                var streams = downloaded.Select(file => new ImportFile(file.Name, File.OpenRead(file.Path), new FileInfo(file.Path).Length)).ToList();
                try
                {
                    var label = downloaded.Count == 1 ? downloaded[0].Name : $"{downloaded.Count} tracks";
                    heard = await import.AudioAsync(db, streams, label, known, keepBoth: false, cancellationToken, keepOrder: true);
                }
                finally
                {
                    foreach (var stream in streams)
                    {
                        await stream.Content.DisposeAsync();
                    }
                }
            }

            if (ebookFile is { } epub && AbsClient.Text(epub, "ino") is { } ino)
            {
                var name = Path.GetFileName(AbsClient.Text(epub.GetProperty("metadata"), "filename") ?? $"book.{AbsClient.Text(epub, "ebookFormat") ?? "epub"}");
                var path = Path.Combine(folder, name);
                await DownloadAsync(http, new Uri(server, $"api/items/{Uri.EscapeDataString(item.Id)}/file/{ino}/download"), token, path, 0, EbookStore.MaxBytes, job, item.Id, cancellationToken);
                await using var stream = File.OpenRead(path);
                read = await import.EbookAsync(db, new ImportFile(name, stream, stream.Length), keepBoth: false, known, cancellationToken);
            }

            var bookId = new[] { heard, read }.FirstOrDefault(result => result?.Outcome is ImportOutcome.Added or ImportOutcome.AddedToExisting)?.BookId;
            var landed = bookId ?? new[] { heard, read }.FirstOrDefault(result => result?.Outcome == ImportOutcome.AlreadyOnShelf)?.SameAsId;
            if (landed is int linked)
            {
                await LinkAsync(db, job, item.Id, linked, cancellationToken);
            }

            if (bookId is null)
            {
                var already = new[] { heard, read }.FirstOrDefault(result => result?.Outcome == ImportOutcome.AlreadyOnShelf);
                Set(job, item.Id, line => already is not null
                    ? line with { State = AbsState.Skipped, BookId = already.SameAsId, Note = T("Already on your shelf.") }
                    : line with { State = AbsState.Failed, Note = audioFiles.Count == 0 && ebookFile is null ? T("It has no audio or e-book to bring.") : T("Shelf could not read its files.") });
                return;
            }

            var book = await db.Books.Include(entry => entry.Tags).FirstAsync(entry => entry.Id == bookId, cancellationToken);
            Fill(book, metadata);
            var genres = metadata.TryGetProperty("genres", out var listed) && listed.ValueKind == JsonValueKind.Array
                ? listed.EnumerateArray().Where(genre => genre.ValueKind == JsonValueKind.String).Select(genre => genre.GetString()!.Trim().ToLowerInvariant()).Where(genre => genre.Length is > 0 and <= BookRules.MaxTagLength).Take(8)
                : [];
            await BookRules.SyncTagsAsync(db, book, [.. book.Tags.Select(tag => tag.Name).Concat(genres).Distinct()], cancellationToken);

            if (!Covers.HasCover(book))
            {
                using var cover = await AbsClient.Send(http, HttpMethod.Get, new Uri(server, $"api/items/{Uri.EscapeDataString(item.Id)}/cover?raw=1"), token, null, cancellationToken);
                if (cover.IsSuccessStatusCode && cover.Content.Headers.ContentLength is null or <= CoverStore.MaxBytes)
                {
                    book.CoverImage = await covers.SaveAsync(await cover.Content.ReadAsByteArrayAsync(cancellationToken), cancellationToken);
                }
            }

            if (job.Life is { } life)
            {
                var lengths = audioFiles.Select(file => AbsClient.Number(file, "duration") ?? 0).ToList();
                await ApplyAsync(db, job, item.Id, book, life, lengths, scope.ServiceProvider.GetRequiredService<EbookStore>(), cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
            Set(job, item.Id, line => line with
            {
                State = AbsState.Done,
                BookId = book.Id,
                Note = tracksLeft > 0
                    ? T("Brought the first {0} of its {1} tracks, the most a book holds; the last {2} stay in Audiobookshelf.", AudioStore.MaxTracks, audioFiles.Count, tracksLeft)
                    : heard?.Outcome == ImportOutcome.AlreadyOnShelf || read?.Outcome == ImportOutcome.AlreadyOnShelf ? T("Some of its files were on your shelf already.") : null,
            });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // The item is known from now on, so keeping in step does not offer it again.
    private static async Task LinkAsync(ShelfDb db, AbsImportJob job, string itemId, int bookId, CancellationToken cancellationToken)
    {
        var server = AbsKeeper.ServerKey(job.Connection.Server);
        if (!await db.AbsLinks.AnyAsync(link => link.ReaderId == job.ReaderId && link.Server == server && link.ItemId == itemId, cancellationToken))
        {
            db.AbsLinks.Add(new AbsLink { ReaderId = job.ReaderId, Server = server, ItemId = itemId, BookId = bookId });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    // What the catalog knows beyond the files: a subtitle, the series, and who reads it.
    private static void Fill(Book book, JsonElement metadata)
    {
        book.Subtitle ??= Fit(AbsClient.Text(metadata, "subtitle"), 200);
        if (book.Series is null && metadata.TryGetProperty("series", out var series) && series.ValueKind == JsonValueKind.Array && series.GetArrayLength() > 0)
        {
            book.Series = Fit(AbsClient.Text(series[0], "name"), 200);
            if (decimal.TryParse(AbsClient.Text(series[0], "sequence"), NumberStyles.Number, CultureInfo.InvariantCulture, out var number) && number == Math.Floor(number) && number is >= 0 and <= 10000)
            {
                book.SeriesNumber = (int)number;
            }
        }

        if (book.Narrator is null && AbsClient.Text(metadata, "narratorName") is { } narrator)
        {
            book.Narrator = Fit(narrator, 200);
        }
    }

    // The books of this import already on the reader's shelf, and which book each is: one brought from this server
    // before, or else one with the same title and author that has the kinds of file the item has. Their files do not
    // come again.
    private async Task<Dictionary<string, int>> OnShelfAsync(AbsImportJob job, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(job.ReaderId);
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var server = AbsKeeper.ServerKey(job.Connection.Server);
        var books = await db.Books.AsNoTracking()
            .Select(book => new { book.Id, book.Title, book.Author, Audio = book.AudioStoredName != null, Ebook = book.EbookStoredName != null })
            .ToListAsync(cancellationToken);
        var ids = books.Select(book => book.Id).ToHashSet();
        var linked = (await db.AbsLinks.AsNoTracking().Where(link => link.ReaderId == job.ReaderId && link.Server == server).ToListAsync(cancellationToken))
            .Where(link => ids.Contains(link.BookId))
            .GroupBy(link => link.ItemId)
            .ToDictionary(group => group.Key, group => group.First().BookId, StringComparer.Ordinal);
        var named = books.ToLookup(book => $"{book.Title.Trim().ToUpperInvariant()}\u0001{book.Author.Trim().ToUpperInvariant()}");
        foreach (var item in job.Items.Where(item => !linked.ContainsKey(item.Id)))
        {
            if (named[$"{item.Title.Trim().ToUpperInvariant()}\u0001{item.Author.Trim().ToUpperInvariant()}"].FirstOrDefault(book => (item.AudioFiles == 0 || book.Audio) && (item.EbookFormat is null || book.Ebook)) is { } same)
            {
                linked[item.Id] = same.Id;
            }
        }

        return linked;
    }

    // A book already on the shelf: no files come, but the reader's progress, bookmarks, and history do, if asked.
    private async Task UpdateAsync(AbsImportJob job, AbsItem item, int bookId, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(job.ReaderId);
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        await LinkAsync(db, job, item.Id, bookId, cancellationToken);
        var book = await db.Books.FirstOrDefaultAsync(entry => entry.Id == bookId, cancellationToken);
        if (book is null || !job.Options.UpdateExisting || job.Life is not { } life)
        {
            Set(job, item.Id, line => line with { State = AbsState.Skipped, BookId = bookId, Note = T("Already on your shelf.") });
            return;
        }

        // Each track's length as kept here, or, where one isn't known, as Audiobookshelf has it.
        var stored = scope.ServiceProvider.GetRequiredService<AudioStore>().Lengths(book.AudioStoredName);
        var lengths = book.AudioStoredName is null || stored.All(length => length is > 0)
            ? stored.Select(length => length ?? 0).ToList()
            : await TrackLengthsAsync(job, item.Id, cancellationToken) ?? stored.Select(length => length ?? 0).ToList();
        var changed = await ApplyAsync(db, job, item.Id, book, life, lengths, scope.ServiceProvider.GetRequiredService<EbookStore>(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        Set(job, item.Id, line => changed
            ? line with { State = AbsState.Done, BookId = bookId, Note = T("Already on your shelf; your reading came across.") }
            : line with { State = AbsState.Skipped, BookId = bookId, Note = T("Already on your shelf.") });
    }

    // How long each of an item's tracks is, in Audiobookshelf's order: its details, not its files.
    private async Task<List<double>?> TrackLengthsAsync(AbsImportJob job, string itemId, CancellationToken cancellationToken)
    {
        using var found = await AbsClient.Send(clients.CreateClient(job.ClientName), HttpMethod.Get, new Uri(job.Connection.Server, $"api/items/{Uri.EscapeDataString(itemId)}?expanded=1"), job.Connection.Token, null, cancellationToken);
        if (!found.IsSuccessStatusCode)
        {
            return null;
        }

        using var document = await JsonDocument.ParseAsync(await found.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("media", out var media) && media.TryGetProperty("audioFiles", out var files) && files.ValueKind == JsonValueKind.Array
            ? [.. files.EnumerateArray()
                .Where(file => !(file.TryGetProperty("exclude", out var excluded) && excluded.ValueKind == JsonValueKind.True))
                .OrderBy(file => file.TryGetProperty("index", out var index) && index.ValueKind == JsonValueKind.Number ? index.GetInt32() : 0)
                .Select(file => AbsClient.Number(file, "duration") ?? 0)]
            : null;
    }

    // The reader's Audiobookshelf life with this book: where they stopped and when they started and finished it, their
    // bookmarks in it, and the days they listened to it, each brought once. Says whether anything changed.
    private static async Task<bool> ApplyAsync(ShelfDb db, AbsImportJob job, string itemId, Book book, AbsReadingLife life, IReadOnlyList<double> lengths, EbookStore ebooks, CancellationToken cancellationToken)
    {
        var changed = false;
        if (job.Options.Progress && life.Progress.TryGetValue(itemId, out var progress))
        {
            changed |= Place(book, progress, lengths, ebooks, db.Today);
        }

        if (job.Options.Bookmarks && book.AudioStoredName is not null && life.Bookmarks.TryGetValue(itemId, out var marks))
        {
            var kept = await db.AudioBookmarks.Where(mark => mark.BookId == book.Id && mark.ReaderId == null).Select(mark => new { mark.Track, mark.Seconds }).ToListAsync(cancellationToken);
            foreach (var mark in marks)
            {
                var (track, seconds) = Locate(mark.Time, lengths);
                if (kept.Any(other => other.Track == track && Math.Abs(other.Seconds - seconds) <= 1))
                {
                    continue;
                }

                db.AudioBookmarks.Add(new AudioBookmark
                {
                    BookId = book.Id,
                    Track = track,
                    Seconds = seconds,
                    Note = Fit(mark.Title, 500),
                    CreatedAt = mark.CreatedAt is long at ? DateTimeOffset.FromUnixTimeMilliseconds(at) : DateTimeOffset.UtcNow,
                });
                kept.Add(new { Track = track, Seconds = seconds });
                changed = true;
            }
        }

        if (job.Options.History && life.History.TryGetValue(itemId, out var days))
        {
            var server = AbsKeeper.ServerKey(job.Connection.Server);
            var link = await db.AbsLinks.FirstOrDefaultAsync(entry => entry.ReaderId == job.ReaderId && entry.Server == server && entry.ItemId == itemId, cancellationToken);
            if (link is { HistoryBrought: false })
            {
                var rows = await db.ListeningDays.IgnoreQueryFilters().Where(row => row.BookId == book.Id && row.ReaderId == job.ReaderId).ToListAsync(cancellationToken);
                foreach (var (day, seconds) in days)
                {
                    if (rows.FirstOrDefault(row => row.Day == day) is { } row)
                    {
                        row.Seconds += seconds;
                    }
                    else
                    {
                        db.ListeningDays.Add(new ListeningDay { BookId = book.Id, ReaderId = job.ReaderId, Day = day, Seconds = seconds });
                    }
                }

                link.HistoryBrought = true;
                changed = true;
            }
        }

        return changed;
    }

    // Where the reader stopped: finished, or a time across the tracks and a place in the e-book; with when they began.
    private static bool Place(Book book, AbsProgress progress, IReadOnlyList<double> lengths, EbookStore ebooks, DateOnly today)
    {
        static DateOnly? Day(long? at) => at is long ms ? DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime) : null;
        var started = Day(progress.StartedAt);
        void Began()
        {
            if (started is { } day && (book.StartedOn is null || book.StartedOn > day))
            {
                book.StartedOn = day;
            }
        }

        if (progress.Finished)
        {
            book.Status = BookStatus.Finished;
            book.FinishedOn ??= Day(progress.FinishedAt);
            Began();
            return true;
        }

        var moved = false;
        if (progress.CurrentTime > 0 && book.AudioStoredName is not null)
        {
            (book.AudioTrack, book.AudioSeconds) = Locate(progress.CurrentTime, lengths);
            moved = true;
        }

        if (book.EbookStoredName is not null && EbookStore.IsEpub(book.EbookStoredName))
        {
            var chapters = EpubFile.Chapters(ebooks.OpenPath(book.EbookStoredName) ?? "")?.Count ?? 0;
            var chapter = AbsClient.SpineIndex(progress.EbookLocation)
                ?? (progress.EbookProgress is double share && share > 0 && chapters > 0 ? (int)Math.Floor(share * chapters) : null);
            if (chapter is { } at && chapters > 0)
            {
                book.EbookChapter = Math.Clamp(at, 0, chapters - 1);
                moved = true;
            }
        }

        if (moved)
        {
            Began();
            if (book.Status == BookStatus.Want)
            {
                book.Status = BookStatus.Reading;
                book.StartedOn ??= today;
            }
        }

        return moved;
    }

    // A time across the whole recording, as a track and a second in it. Lengths not known count as none.
    private static (int Track, int Seconds) Locate(double time, IReadOnlyList<double> lengths)
    {
        if (lengths.Count == 0 || lengths.Sum() <= 0)
        {
            return (0, (int)Math.Max(0, Math.Floor(time)));
        }

        var left = time;
        for (var track = 0; track < lengths.Count; track++)
        {
            if (left < lengths[track] || track == lengths.Count - 1)
            {
                return (track, (int)Math.Max(0, Math.Floor(left)));
            }

            left -= lengths[track];
        }

        return (0, 0);
    }

    // Audiobookshelf's collections and playlists, as collections of the books on the reader's shelf that came from it.
    private async Task ListsAsync(AbsImportJob job, CancellationToken cancellationToken)
    {
        if (!job.Options.Collections || job.Life is not { Lists.Count: > 0 } life)
        {
            return;
        }

        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(job.ReaderId);
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var server = AbsKeeper.ServerKey(job.Connection.Server);
        var linked = (await db.AbsLinks.AsNoTracking().Where(link => link.ReaderId == job.ReaderId && link.Server == server).ToListAsync(cancellationToken))
            .GroupBy(link => link.ItemId).ToDictionary(group => group.Key, group => group.First().BookId, StringComparer.Ordinal);
        foreach (var list in life.Lists)
        {
            var books = list.ItemIds.Where(linked.ContainsKey).Select(id => linked[id]).Distinct().ToList();
            if (books.Count == 0)
            {
                continue;
            }

            var name = Fit(list.Name, Collection.MaxNameLength) ?? "Audiobookshelf";
            var collection = await db.Collections.Include(item => item.Books).FirstOrDefaultAsync(item => item.OwnerId == job.ReaderId && item.Name == name, cancellationToken);
            if (collection is null)
            {
                var (made, _) = await Collections.CreateAsync(db, name, T("Brought from Audiobookshelf."), cancellationToken);
                if (made is null)
                {
                    continue;
                }

                collection = await db.Collections.Include(item => item.Books).FirstAsync(item => item.Id == made.Id, cancellationToken);
            }

            var position = collection.Books.Count == 0 ? 0 : collection.Books.Max(item => item.Position) + 1;
            var added = false;
            foreach (var bookId in books.Where(id => collection.Books.All(item => item.BookId != id)).Take(Collection.MaxBooks - collection.Books.Count))
            {
                collection.Books.Add(new CollectionBook { BookId = bookId, Position = position++, AddedAt = DateTimeOffset.UtcNow });
                added = true;
            }

            await db.SaveChangesAsync(cancellationToken);
            job.ListsMade += added ? 1 : 0;
        }
    }

    private static async Task<long> DownloadAsync(HttpClient http, Uri url, string token, string path, long total, long limit, AbsImportJob job, string itemId, CancellationToken cancellationToken)
    {
        using var response = await AbsClient.Send(http, HttpMethod.Get, url, token, null, cancellationToken, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new AbsProblem(T("This Audiobookshelf user may not download. Turn on Can Download for them in Audiobookshelf's Users settings."));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new AbsProblem(T("Audiobookshelf answered {0} for one of its files.", (int)response.StatusCode));
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(path);
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new AbsProblem(T("It is larger than Shelf takes ({0} MB).", limit / 1024 / 1024));
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            Set(job, itemId, line => line with { Bytes = total });
        }

        return total;
    }

    private async Task NoteAsync(AbsImportJob job, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(job.ReaderId);
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var done = job.Books.Values.Count(line => line.State == AbsState.Done);
        var whose = job.ForName is null ? "" : $" for {job.ForName}";
        await Audit.NoteAsync(db, Localization.Words.Say("Imported from Audiobookshelf"), detail: $"{done} of {job.Items.Count} books from {job.Connection.Server.GetLeftPart(UriPartial.Authority)}{whose}", cancellationToken: cancellationToken);
    }

    private static string? Fit(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length > length ? value[..length].Trim() : value.Trim();
}

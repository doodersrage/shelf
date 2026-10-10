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
using Shelf.Api.Readers;

namespace Shelf.Api.Books;

public sealed record AbsLibrary(string Id, string Name);

// A book in an Audiobookshelf library, as its list describes it.
public sealed record AbsItem(string Id, string Title, string Author, int AudioFiles, string? EbookFormat, double Duration);

public sealed record AbsConnection(Uri Server, string Token, string User, bool CanDownload, IReadOnlyList<AbsLibrary> Libraries);

public sealed class AbsProblem(string message) : Exception(message);

// Talking to an Audiobookshelf server (2.x) with a user's API key or access token, sent as a Bearer token.
public static partial class AbsClient
{
    public const string ClientName = "audiobookshelf";

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
                throw new AbsProblem("Audiobookshelf did not accept that user name and password.");
            }

            await Ensure(login);
            using var answer = await JsonDocument.ParseAsync(await login.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            token = Text(answer.RootElement.GetProperty("user"), "accessToken") ?? Text(answer.RootElement.GetProperty("user"), "token")
                ?? throw new AbsProblem("Audiobookshelf signed in but sent no token back.");
        }

        using var authorize = await Send(http, HttpMethod.Post, new Uri(server, "api/authorize"), token, null, cancellationToken);
        if (authorize.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new AbsProblem("Audiobookshelf did not accept that API key. Check that it is turned on (active) in Settings, API Keys.");
        }

        await Ensure(authorize);
        using var me = await JsonDocument.ParseAsync(await authorize.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var account = me.RootElement.GetProperty("user");
        var canDownload = account.TryGetProperty("permissions", out var permissions)
            && permissions.TryGetProperty("download", out var download) && download.ValueKind == JsonValueKind.True;

        using var listed = await Send(http, HttpMethod.Get, new Uri(server, "api/libraries"), token, null, cancellationToken);
        await Ensure(listed);
        using var libraries = await JsonDocument.ParseAsync(await listed.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var books = libraries.RootElement.GetProperty("libraries").EnumerateArray()
            .Where(library => Text(library, "mediaType") == "book")
            .Select(library => new AbsLibrary(Text(library, "id")!, Text(library, "name") ?? "Library"))
            .ToList();
        return new AbsConnection(server, token, Text(account, "username") ?? "", canDownload, books);
    }

    // Every book in a library, two hundred at a time.
    public static async Task<List<AbsItem>> ItemsAsync(HttpClient http, AbsConnection connection, string libraryId, CancellationToken cancellationToken)
    {
        var items = new List<AbsItem>();
        for (var page = 0; page < 500; page++)
        {
            var url = new Uri(connection.Server, $"api/libraries/{Uri.EscapeDataString(libraryId)}/items?limit=200&page={page}&sort=media.metadata.title");
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
        catch (HttpRequestException)
        {
            throw new AbsProblem($"Shelf could not reach {url.GetLeftPart(UriPartial.Authority)}. Check the address, and that this server can reach it.");
        }
    }

    private static async Task Ensure(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.NotFound && response.RequestMessage?.RequestUri?.AbsolutePath.EndsWith("/api/libraries", StringComparison.Ordinal) == true)
        {
            throw new AbsProblem("That address does not answer as Audiobookshelf. If it is served under a path, include it, such as https://example.org/audiobookshelf.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var reason = response.Content is null ? "" : (await response.Content.ReadAsStringAsync()).Trim();
            throw new AbsProblem($"Audiobookshelf answered {(int)response.StatusCode}{(reason.Length is > 0 and < 200 ? $": {reason}" : ".")}");
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

public sealed class AbsImportJob(Guid id, int readerId, AbsConnection connection, bool progress, IReadOnlyList<AbsItem> items)
{
    public Guid Id { get; } = id;
    public int ReaderId { get; } = readerId;
    public AbsConnection Connection { get; } = connection;
    public bool Progress { get; } = progress;
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
    public ConcurrentDictionary<string, AbsImportLine> Lines { get; } = new(items.Select(item => KeyValuePair.Create(item.Id, new AbsImportLine(item.Id, item.Title, AbsState.Waiting, 0, null, null))));
    public IReadOnlyList<AbsItem> Items { get; } = items;
    public bool Finished { get; set; }
}

// Imports run one at a time in the background, each book in turn: its tracks in order, its e-book, its cover, and,
// when asked, where the reader stopped. The key or token is held only until the import ends.
public sealed class AbsImporter(IServiceScopeFactory scopes, IHttpClientFactory clients, ILogger<AbsImporter> logger) : BackgroundService
{
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, AbsImportJob> _jobs = new();

    public AbsImportJob Start(int readerId, AbsConnection connection, bool progress, IReadOnlyList<AbsItem> items)
    {
        var job = new AbsImportJob(Guid.NewGuid(), readerId, connection, progress, items);
        _jobs[job.Id] = job;
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

            foreach (var item in job.Items)
            {
                try
                {
                    await ImportAsync(job, item, stoppingToken);
                }
                catch (Exception ex) when (ex is AbsProblem or HttpRequestException or IOException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }

                    logger.LogInformation(ex, "Could not import {Title} from Audiobookshelf.", item.Title);
                    Set(job, item.Id, line => line with { State = AbsState.Failed, Note = ex is AbsProblem ? ex.Message : "The download did not finish." });
                }
            }

            job.Finished = true;
            await NoteAsync(job, stoppingToken);
        }
    }

    private static void Set(AbsImportJob job, string itemId, Func<AbsImportLine, AbsImportLine> change) =>
        job.Lines.AddOrUpdate(itemId, _ => throw new KeyNotFoundException(), (_, line) => change(line));

    private async Task ImportAsync(AbsImportJob job, AbsItem item, CancellationToken cancellationToken)
    {
        var http = clients.CreateClient(AbsClient.ClientName);
        var server = job.Connection.Server;
        var token = job.Connection.Token;
        Set(job, item.Id, line => line with { State = AbsState.Downloading });

        using var found = await AbsClient.Send(http, HttpMethod.Get, new Uri(server, $"api/items/{Uri.EscapeDataString(item.Id)}?expanded=1"), token, null, cancellationToken);
        if (!found.IsSuccessStatusCode)
        {
            throw new AbsProblem($"Audiobookshelf answered {(int)found.StatusCode} for this book.");
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

            if (audioFiles.Count > 0)
            {
                var downloaded = new List<(string Path, string Name)>();
                foreach (var (file, position) in audioFiles.Select((file, position) => (file, position)))
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
            if (bookId is null)
            {
                var already = new[] { heard, read }.FirstOrDefault(result => result?.Outcome == ImportOutcome.AlreadyOnShelf);
                Set(job, item.Id, line => already is not null
                    ? line with { State = AbsState.Skipped, BookId = already.SameAsId, Note = "Already on your shelf." }
                    : line with { State = AbsState.Failed, Note = audioFiles.Count == 0 && ebookFile is null ? "It has no audio or e-book to bring." : "Shelf could not read its files." });
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

            if (job.Progress)
            {
                await ProgressAsync(http, server, token, item.Id, book, audioFiles, scope.ServiceProvider.GetRequiredService<EbookStore>(), cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
            Set(job, item.Id, line => line with { State = AbsState.Done, BookId = book.Id, Note = heard?.Outcome == ImportOutcome.AlreadyOnShelf || read?.Outcome == ImportOutcome.AlreadyOnShelf ? "Some of its files were on your shelf already." : null });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
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

        if (book.Notes is null && AbsClient.Text(metadata, "narratorName") is { } narrator)
        {
            book.Notes = $"Read by {narrator}.";
        }
    }

    // Where the reader stopped in Audiobookshelf: a time across the tracks, a place in the e-book, or finished.
    private static async Task ProgressAsync(HttpClient http, Uri server, string token, string itemId, Book book, List<JsonElement> audioFiles, EbookStore ebooks, CancellationToken cancellationToken)
    {
        using var response = await AbsClient.Send(http, HttpMethod.Get, new Uri(server, $"api/me/progress/{Uri.EscapeDataString(itemId)}"), token, null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return;
        }

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var progress = document.RootElement;
        if (progress.TryGetProperty("isFinished", out var finished) && finished.ValueKind == JsonValueKind.True)
        {
            book.Status = BookStatus.Finished;
            if (progress.TryGetProperty("finishedAt", out var at) && at.ValueKind == JsonValueKind.Number)
            {
                book.FinishedOn ??= DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(at.GetInt64()).UtcDateTime);
            }

            return;
        }

        var moved = false;
        if (progress.TryGetProperty("currentTime", out var time) && time.ValueKind == JsonValueKind.Number && time.GetDouble() > 0 && book.AudioStoredName is not null)
        {
            var left = time.GetDouble();
            for (var track = 0; track < audioFiles.Count; track++)
            {
                var length = audioFiles[track].TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.Number ? duration.GetDouble() : 0;
                if (left < length || track == audioFiles.Count - 1)
                {
                    book.AudioTrack = track;
                    book.AudioSeconds = (int)Math.Max(0, Math.Floor(left));
                    break;
                }

                left -= length;
            }

            moved = true;
        }

        if (book.EbookStoredName is not null && EbookStore.IsEpub(book.EbookStoredName))
        {
            var chapters = EpubFile.Chapters(ebooks.OpenPath(book.EbookStoredName) ?? "")?.Count ?? 0;
            var chapter = AbsClient.SpineIndex(AbsClient.Text(progress, "ebookLocation"))
                ?? (progress.TryGetProperty("ebookProgress", out var share) && share.ValueKind == JsonValueKind.Number && share.GetDouble() > 0 && chapters > 0
                    ? (int)Math.Floor(share.GetDouble() * chapters)
                    : null);
            if (chapter is { } at && chapters > 0)
            {
                book.EbookChapter = Math.Clamp(at, 0, chapters - 1);
                moved = true;
            }
        }

        if (moved && book.Status == BookStatus.Want)
        {
            book.Status = BookStatus.Reading;
            book.StartedOn ??= DateOnly.FromDateTime(DateTime.UtcNow);
        }
    }

    private static async Task<long> DownloadAsync(HttpClient http, Uri url, string token, string path, long total, long limit, AbsImportJob job, string itemId, CancellationToken cancellationToken)
    {
        using var response = await AbsClient.Send(http, HttpMethod.Get, url, token, null, cancellationToken, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new AbsProblem("This Audiobookshelf user may not download. Turn on Can Download for them in Audiobookshelf's Users settings.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new AbsProblem($"Audiobookshelf answered {(int)response.StatusCode} for one of its files.");
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
                throw new AbsProblem($"It is larger than Shelf takes ({limit / 1024 / 1024} MB).");
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
        var done = job.Lines.Values.Count(line => line.State == AbsState.Done);
        await Audit.NoteAsync(db, "Imported from Audiobookshelf", detail: $"{done} of {job.Items.Count} books from {job.Connection.Server.GetLeftPart(UriPartial.Authority)}", cancellationToken: cancellationToken);
    }

    private static string? Fit(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length > length ? value[..length].Trim() : value.Trim();
}

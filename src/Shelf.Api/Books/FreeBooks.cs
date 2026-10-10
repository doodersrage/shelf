using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Xml;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Localization;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public enum FreeSource
{
    Gutenberg,
    LibriVox,
}

// A public-domain book found in a free catalog: an e-book from Project Gutenberg, or a LibriVox recording.
public sealed record FreeBook(FreeSource Source, string Id, string Title, string Author, string? Language = null, string? Length = null, string? CoverUrl = null);

public enum DownloadState
{
    Waiting,
    Downloading,
    Done,
    Failed,
}

public sealed record FreeDownload(Guid Id, int ReaderId, FreeBook Book, DownloadState State, long Bytes, int? BookId, string? Problem, DateTimeOffset Started, string Language);

public static partial class FreeCatalog
{
    public const string ClientName = "free-books";
    private const string Gutenberg = "https://www.gutenberg.org";
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    // Project Gutenberg's OPDS search: each entry's id names the book's number, its content the author.
    public static async Task<List<FreeBook>> SearchGutenbergAsync(HttpClient http, string query, CancellationToken cancellationToken)
    {
        var url = $"{Gutenberg}/ebooks/search.opds/?query={Uri.EscapeDataString(query)}";
        return (await ReadGutenbergFeedAsync(http, url, cancellationToken)).Books;
    }

    // LibriVox's catalog, by title and by author's last name, merged.
    public static async Task<List<FreeBook>> SearchLibriVoxAsync(HttpClient http, string query, CancellationToken cancellationToken)
    {
        var found = new List<FreeBook>();
        foreach (var field in new[] { "title", "author" })
        {
            // LibriVox files titles without a leading article ("Raven"), and matches a whole title unless asked with ^ to match its start.
            var term = field == "author"
                ? query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? ""
                : "^" + LeadingArticle().Replace(query.Trim(), "");
            if (term.Trim('^').Length == 0)
            {
                continue;
            }

            var url = $"https://librivox.org/api/feed/audiobooks/?{field}={Uri.EscapeDataString(term)}&format=json&limit=20&fields=%7Bid,title,authors,language,totaltime,url_zip_file%7D";
            using var response = await http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // LibriVox answers a search with no matches with 404.
                continue;
            }

            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("books", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var book in list.EnumerateArray())
            {
                var id = Text(book, "id");
                var title = Text(book, "title");
                if (id is null || title is null || Text(book, "url_zip_file") is null || found.Any(item => item.Id == id))
                {
                    continue;
                }

                var authors = book.TryGetProperty("authors", out var people) && people.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", people.EnumerateArray().Select(person => $"{Text(person, "first_name")} {Text(person, "last_name")}".Trim()).Where(name => name.Length > 0))
                    : "";
                found.Add(new FreeBook(FreeSource.LibriVox, id, title, authors.Length == 0 ? "Unknown author" : authors, Text(book, "language"), Text(book, "totaltime")));
            }
        }

        return found;
    }

    // Where to download one: Gutenberg's EPUB with images, or the LibriVox recording's zip of MP3s at archive.org.
    public static async Task<string?> DownloadUrlAsync(HttpClient http, FreeBook book, CancellationToken cancellationToken)
    {
        if (book.Source == FreeSource.Gutenberg)
        {
            return $"{Gutenberg}/ebooks/{Uri.EscapeDataString(book.Id)}.epub3.images";
        }

        var url = $"https://librivox.org/api/feed/audiobooks/?id={Uri.EscapeDataString(book.Id)}&format=json&fields=%7Burl_zip_file%7D";
        using var response = await http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("books", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0
            ? Text(list[0], "url_zip_file")
            : null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text.Trim() : null;

    [GeneratedRegex(@"^(the|a|an)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingArticle();

    [GeneratedRegex(@"/ebooks/(\d+)(\.opds)?$")]
    private static partial Regex GutenbergNumber();
}

// Downloads from the free catalogs, one at a time in the background, so a large recording does not hold a request open.
public sealed class FreeBooks(IServiceScopeFactory scopes, IHttpClientFactory clients, IConfiguration configuration, ILogger<FreeBooks> logger) : BackgroundService
{
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, FreeDownload> _downloads = new();

    public bool Enabled => configuration.GetValue("FreeBooks:Enabled", true);

    public FreeDownload Enqueue(int readerId, FreeBook book)
    {
        // Kept with the language of the page that asked, so its messages and notes come out in that language.
        var download = new FreeDownload(Guid.NewGuid(), readerId, book, DownloadState.Waiting, 0, null, null, DateTimeOffset.UtcNow, Words.Current);
        _downloads[download.Id] = download;
        _queue.Writer.TryWrite(download.Id);
        return download;
    }

    // This reader's downloads from the last day, newest first.
    public IReadOnlyList<FreeDownload> For(int readerId)
    {
        foreach (var old in _downloads.Values.Where(download => download.State is DownloadState.Done or DownloadState.Failed && download.Started < DateTimeOffset.UtcNow.AddDays(-1)))
        {
            _downloads.TryRemove(old.Id, out _);
        }

        return _downloads.Values.Where(download => download.ReaderId == readerId).OrderByDescending(download => download.Started).ToList();
    }

    public FreeDownload? Find(Guid id, int readerId) =>
        _downloads.TryGetValue(id, out var download) && download.ReaderId == readerId ? download : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (!_downloads.TryGetValue(id, out var download))
            {
                continue;
            }

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(download.Language);

            try
            {
                var bookId = await DownloadAsync(download, stoppingToken);
                _downloads[id] = _downloads[id] with { State = DownloadState.Done, BookId = bookId };
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or InvalidOperationException or FreeBookProblem)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                logger.LogInformation(ex, "Could not add {Title} from {Source}.", download.Book.Title, download.Book.Source);
                _downloads[id] = _downloads[id] with { State = DownloadState.Failed, Problem = ex is FreeBookProblem problem ? problem.Message : T("The download did not finish. Try again later.") };
            }
        }
    }

    private async Task<int> DownloadAsync(FreeDownload download, CancellationToken cancellationToken)
    {
        var http = clients.CreateClient(FreeCatalog.ClientName);
        var url = await FreeCatalog.DownloadUrlAsync(http, download.Book, cancellationToken)
            ?? throw new FreeBookProblem(T("LibriVox has no download for that recording."));
        var audio = download.Book.Source == FreeSource.LibriVox;
        var limit = audio ? AudioStore.MaxBytes : EbookStore.MaxBytes;
        var folder = Path.Combine(Path.GetTempPath(), $"shelf-free-{download.Id:N}");
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, audio ? "recording.zip" : "book.epub");
            _downloads[download.Id] = _downloads[download.Id] with { State = DownloadState.Downloading };
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new FreeBookProblem(response.StatusCode == System.Net.HttpStatusCode.NotFound
                        ? T("That book has no file to download.")
                        : T("The catalog answered {0}. Try again later.", (int)response.StatusCode));
                }

                if (response.Content.Headers.ContentLength > limit)
                {
                    throw new FreeBookProblem(T("It is larger than Shelf takes ({0} MB).", limit / 1024 / 1024));
                }

                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = File.Create(file);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > limit)
                    {
                        throw new FreeBookProblem(T("It is larger than Shelf takes ({0} MB).", limit / 1024 / 1024));
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    _downloads[download.Id] = _downloads[download.Id] with { Bytes = total };
                }
            }

            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(download.ReaderId);
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var import = scope.ServiceProvider.GetRequiredService<BookImport>();
            var name = SafeName(download.Book.Title) + (audio ? ".zip" : ".epub");

            // The catalog's title and author lead. Gutenberg's EPUB date is when it was put online, not written, so it is left out.
            var fromFile = audio ? null : EpubFile.Details(file);
            var known = new FileDetails(download.Book.Title, download.Book.Author, fromFile?.Isbn, fromFile?.Publisher, null, fromFile?.Language ?? download.Book.Language);
            ImportedFile result;
            await using (var stream = File.OpenRead(file))
            {
                var upload = new ImportFile(name, stream, stream.Length);
                result = audio
                    ? await import.AudioAsync(db, [upload], name, known, keepBoth: false, cancellationToken)
                    : await import.EbookAsync(db, upload, keepBoth: false, known, cancellationToken);
            }

            if (result.Outcome == ImportOutcome.AlreadyOnShelf)
            {
                throw new FreeBookProblem(T("That file is already on {0}.", result.Title));
            }

            if (result.BookId is not { } bookId || result.Outcome is not (ImportOutcome.Added or ImportOutcome.AddedToExisting))
            {
                throw new FreeBookProblem(T("Shelf could not read the file it downloaded."));
            }

            var book = await db.Books.Include(item => item.Tags).FirstAsync(item => item.Id == bookId, cancellationToken);
            if (result.Outcome == ImportOutcome.Added)
            {
                book.Notes ??= audio
                    ? T("A LibriVox recording, read by volunteers (librivox.org, #{0}).", download.Book.Id)
                    : T("From Project Gutenberg (gutenberg.org, #{0}).", download.Book.Id);
            }

            await BookRules.SyncTagsAsync(db, book, [.. book.Tags.Select(tag => tag.Name), T("public domain")], cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return bookId;
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // A title made fit to be a file name.
    private static string SafeName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var name = Regex.Replace(new string(title.Select(character => invalid.Contains(character) || char.IsControl(character) ? ' ' : character).ToArray()), " {2,}", " ").Trim().TrimEnd('.');
        return name.Length == 0 ? "book" : name.Length > 120 ? name[..120].Trim() : name;
    }

}

public sealed class FreeBookProblem(string message) : Exception(message);

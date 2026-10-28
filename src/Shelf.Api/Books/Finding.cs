using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// A book a catalog knows, found by a search: enough to show it, and to put it on the want list.
public sealed record CatalogHit(string Key, string Title, string Author, int? Year, string? Isbn, string? CoverUrl, int? Pages);

public interface IBookSearch
{
    // Books by title, author, or ISBN, the likeliest first.
    Task<IReadOnlyList<CatalogHit>> SearchAsync(string query, CancellationToken cancellationToken);

    // An author's books, the newest first.
    Task<IReadOnlyList<CatalogHit>> ByAuthorAsync(string author, CancellationToken cancellationToken);

    // After a search that found nothing: true when the catalog couldn't be asked, rather than had nothing.
    bool CouldNotAsk => false;
}

// Open Library's search, which covers far more books than any one shelf.
public sealed class OpenLibrarySearch(HttpClient http) : IBookSearch
{
    private const string Fields = "key,title,author_name,first_publish_year,isbn,cover_i,number_of_pages_median";

    public bool CouldNotAsk { get; private set; }

    public Task<IReadOnlyList<CatalogHit>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var isbn = BookRules.NormalizeIsbn(query);
        var term = isbn is not null ? $"isbn={isbn}" : $"q={Uri.EscapeDataString(query.Trim())}";
        return AskAsync($"search.json?{term}&limit=24&fields={Fields}", cancellationToken);
    }

    public Task<IReadOnlyList<CatalogHit>> ByAuthorAsync(string author, CancellationToken cancellationToken) =>
        AskAsync($"search.json?author={Uri.EscapeDataString(author.Trim())}&sort=new&limit=30&fields={Fields}", cancellationToken);

    private async Task<IReadOnlyList<CatalogHit>> AskAsync(string path, CancellationToken cancellationToken)
    {
        CouldNotAsk = false;
        try
        {
            using var response = await http.GetAsync(path, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                CouldNotAsk = response.StatusCode is System.Net.HttpStatusCode.TooManyRequests or >= System.Net.HttpStatusCode.InternalServerError;
                return [];
            }

            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var hits = new List<CatalogHit>();
            foreach (var doc in document.RootElement.GetProperty("docs").EnumerateArray())
            {
                var title = doc.TryGetProperty("title", out var named) ? named.GetString() : null;
                var key = doc.TryGetProperty("key", out var work) ? work.GetString() : null;
                if (string.IsNullOrWhiteSpace(title) || key is null)
                {
                    continue;
                }

                var authors = doc.TryGetProperty("author_name", out var names) && names.ValueKind == JsonValueKind.Array ? names.EnumerateArray().Select(item => item.GetString()).OfType<string>().Take(2).ToList() : [];
                var isbns = doc.TryGetProperty("isbn", out var numbers) && numbers.ValueKind == JsonValueKind.Array ? numbers.EnumerateArray().Select(item => item.GetString()).OfType<string>().ToList() : [];
                hits.Add(new CatalogHit(
                    key,
                    title.Trim(),
                    authors.Count > 0 ? string.Join(" & ", authors) : BookImport.UnknownAuthor,
                    doc.TryGetProperty("first_publish_year", out var year) && year.TryGetInt32(out var published) ? published : null,
                    isbns.FirstOrDefault(item => item.Length == 13) ?? isbns.FirstOrDefault(),
                    doc.TryGetProperty("cover_i", out var cover) && cover.TryGetInt64(out var coverId) ? $"https://covers.openlibrary.org/b/id/{coverId}-M.jpg" : null,
                    doc.TryGetProperty("number_of_pages_median", out var pages) && pages.TryGetInt32(out var count) && count > 0 ? count : null));
            }

            return hits;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or Polly.ExecutionRejectedException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            CouldNotAsk = ex is HttpRequestException or TaskCanceledException or Polly.ExecutionRejectedException;
            return [];
        }
    }
}

// An author a reader follows: their new books are looked for once a day, offered, or wanted and fetched straight away.
public sealed class AuthorFollow
{
    public int Id { get; set; }
    public int ReaderId { get; set; }
    public required string Name { get; set; }
    public bool AutoFetch { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
}

// A new book by an author followed, waiting to be wanted or let go.
public sealed class AuthorAlert
{
    public int Id { get; set; }
    public int ReaderId { get; set; }
    public required string Key { get; set; }
    public required string Author { get; set; }
    public required string Title { get; set; }
    public int? Year { get; set; }
    public string? Isbn { get; set; }
    public string? CoverUrl { get; set; }
    public DateTimeOffset FoundAt { get; set; }
    public bool Dismissed { get; set; }
}

public static class Finding
{
    public static string Key(string title, string author) => $"{Tidy.Plain(title)}\u0001{Tidy.AuthorKey(author)}";

    // The books on the reader's shelf, by title and author, to mark what a search finds that is here already.
    public static async Task<HashSet<string>> ShelfKeysAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        [.. (await db.Books.AsNoTracking().Select(book => new { book.Title, book.Author }).ToListAsync(cancellationToken)).Select(book => Key(book.Title, book.Author))];

    // A book found in a catalog, onto the want list with what the catalog knows of it.
    public static async Task<Book> WantAsync(ShelfDb db, CatalogHit hit, CancellationToken cancellationToken = default)
    {
        var book = new Book
        {
            Title = hit.Title.Length > 200 ? hit.Title[..200] : hit.Title,
            Author = hit.Author.Length > 200 ? hit.Author[..200] : hit.Author,
            Status = BookStatus.Want,
            Year = hit.Year is >= 1000 and <= 2100 ? hit.Year : null,
            Pages = hit.Pages is >= 1 and <= 20000 ? hit.Pages : null,
            Isbn = BookRules.NormalizeIsbn(hit.Isbn),
            CoverUrl = hit.CoverUrl is { Length: <= 500 } cover && BookRules.IsCoverUrl(cover) ? cover : null,
        };
        db.Books.Add(book);
        await db.SaveChangesAsync(cancellationToken);
        return book;
    }

    public static async Task<IReadOnlyList<AuthorFollow>> FollowsAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        await db.AuthorFollows.AsNoTracking().Where(item => item.ReaderId == db.ReaderId).OrderBy(item => item.Name).ToListAsync(cancellationToken);

    public static async Task FollowAsync(ShelfDb db, string name, CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > 200 || trimmed == BookImport.UnknownAuthor)
        {
            return;
        }

        var key = Tidy.AuthorKey(trimmed);
        var follows = await db.AuthorFollows.Where(item => item.ReaderId == db.ReaderId).ToListAsync(cancellationToken);
        if (follows.Any(item => Tidy.AuthorKey(item.Name) == key))
        {
            return;
        }

        db.AuthorFollows.Add(new AuthorFollow { ReaderId = db.ReaderId, Name = trimmed, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task UnfollowAsync(ShelfDb db, int id, CancellationToken cancellationToken = default) =>
        await db.AuthorFollows.Where(item => item.Id == id && item.ReaderId == db.ReaderId).ExecuteDeleteAsync(cancellationToken);

    public static async Task SetAutoFetchAsync(ShelfDb db, int id, bool on, CancellationToken cancellationToken = default) =>
        await db.AuthorFollows.Where(item => item.Id == id && item.ReaderId == db.ReaderId).ExecuteUpdateAsync(set => set.SetProperty(item => item.AutoFetch, on), cancellationToken);

    public static async Task<IReadOnlyList<AuthorAlert>> AlertsAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        (await db.AuthorAlerts.AsNoTracking().Where(item => item.ReaderId == db.ReaderId && !item.Dismissed).ToListAsync(cancellationToken))
            .OrderByDescending(item => item.FoundAt).ToList();

    public static async Task DismissAsync(ShelfDb db, int id, CancellationToken cancellationToken = default) =>
        await db.AuthorAlerts.Where(item => item.Id == id && item.ReaderId == db.ReaderId).ExecuteUpdateAsync(set => set.SetProperty(item => item.Dismissed, true), cancellationToken);

    // An offered book, onto the want list; the offer goes.
    public static async Task<Book?> WantAlertAsync(ShelfDb db, int id, CancellationToken cancellationToken = default)
    {
        var alert = await db.AuthorAlerts.FirstOrDefaultAsync(item => item.Id == id && item.ReaderId == db.ReaderId && !item.Dismissed, cancellationToken);
        if (alert is null)
        {
            return null;
        }

        alert.Dismissed = true;
        return await WantAsync(db, new CatalogHit(alert.Key, alert.Title, alert.Author, alert.Year, alert.Isbn, alert.CoverUrl, null), cancellationToken);
    }
}

// Once a day, each followed author's newest books: those published since they were followed, and not on the shelf
// or offered before, are offered, or put on the want list and fetched when the reader asked for that.
public sealed class AuthorWatch(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<AuthorWatch> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("AuthorWatch:Enabled", true))
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(configuration.GetValue("AuthorWatch:LookMinutes", 30)));
        do
        {
            try
            {
                await LookAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Looking for followed authors' new books failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task LookAsync(CancellationToken cancellationToken)
    {
        List<int> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var before = DateTimeOffset.UtcNow - Every;
            due = (await db.AuthorFollows.AsNoTracking().ToListAsync(cancellationToken)).Where(item => item.CheckedAt is null || item.CheckedAt < before).Select(item => item.Id).ToList();
        }

        foreach (var id in due)
        {
            await CheckAsync(id, cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(configuration.GetValue("AuthorWatch:PauseMilliseconds", 1500)), cancellationToken);
        }
    }

    // Returns how many new books were found.
    public async Task<int> CheckAsync(int followId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var follow = await db.AuthorFollows.FirstOrDefaultAsync(item => item.Id == followId, cancellationToken);
        if (follow is null)
        {
            return 0;
        }

        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(follow.ReaderId);
        follow.CheckedAt = DateTimeOffset.UtcNow;
        var search = scope.ServiceProvider.GetRequiredService<IBookSearch>();
        var since = follow.CreatedAt.Year;
        var author = Tidy.AuthorKey(follow.Name);
        var shelf = await Finding.ShelfKeysAsync(db, cancellationToken);
        var offered = (await db.AuthorAlerts.AsNoTracking().Where(item => item.ReaderId == follow.ReaderId).Select(item => item.Key).ToListAsync(cancellationToken)).ToHashSet();
        var fresh = (await search.ByAuthorAsync(follow.Name, cancellationToken))
            .Where(hit => hit.Year >= since && Tidy.AuthorKey(hit.Author).Contains(author.Split(' ').Last(), StringComparison.Ordinal))
            .Where(hit => !shelf.Contains(Finding.Key(hit.Title, hit.Author)) && !offered.Contains(hit.Key))
            .DistinctBy(hit => Tidy.Plain(hit.Title))
            .ToList();
        var wantAtOnce = follow.AutoFetch && !await Family.IsRestrictedAsync(db, cancellationToken: cancellationToken);
        foreach (var hit in fresh)
        {
            db.AuthorAlerts.Add(new AuthorAlert
            {
                ReaderId = follow.ReaderId,
                Key = hit.Key.Length > 100 ? hit.Key[..100] : hit.Key,
                Author = hit.Author.Length > 200 ? hit.Author[..200] : hit.Author,
                Title = hit.Title.Length > 200 ? hit.Title[..200] : hit.Title,
                Year = hit.Year,
                Isbn = BookRules.NormalizeIsbn(hit.Isbn),
                CoverUrl = hit.CoverUrl is { Length: <= 500 } cover ? cover : null,
                FoundAt = DateTimeOffset.UtcNow,
                // Wanted straight away, below, when the reader asked for it.
                Dismissed = wantAtOnce,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        // A child's account never fetches, whatever was ticked before it was one.
        if (wantAtOnce)
        {
            var fetcher = scope.ServiceProvider.GetRequiredService<Fetcher>();
            var settings = await FetchRules.SettingsAsync(db, cancellationToken);
            foreach (var hit in fresh)
            {
                var book = await Finding.WantAsync(db, hit, cancellationToken);
                if (Fetcher.Ready(settings))
                {
                    try
                    {
                        if ((await fetcher.SearchAsync(settings, book.Title, book.Author, settings.WantedKind, cancellationToken)).FirstOrDefault(item => item.Matches) is { } best)
                        {
                            await fetcher.GrabAsync(db, settings, book, settings.WantedKind, best.Release, cancellationToken);
                        }
                    }
                    catch (Exception ex) when (ex is FetchProblem or HttpRequestException or TaskCanceledException or JsonException)
                    {
                        logger.LogInformation(ex, "Could not fetch {Title} for a followed author.", book.Title);
                    }
                }
            }
        }

        // Noted only when something was found, so a daily look at many authors doesn't crowd the activity log.
        if (fresh.Count > 0)
        {
            await Audit.NoteAsync(db, Say("Looked for new books by an author"), detail: $"{follow.Name}: {fresh.Count}", cancellationToken: cancellationToken);
        }

        return fresh.Count;
    }
}

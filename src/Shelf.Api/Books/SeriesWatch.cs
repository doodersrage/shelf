using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// A book in a series the reader follows that is not on their shelf, found by the daily look at Open Library.
public sealed class SeriesAlert
{
    public int Id { get; set; }
    public int OwnerId { get; set; }
    public required string Series { get; set; }
    public required string Author { get; set; }
    public required string Title { get; set; }
    public int? Year { get; set; }

    // Open Library's work key, such as /works/OL20805971W, so the same book is never found twice.
    public required string WorkKey { get; set; }
    public DateTimeOffset FoundAt { get; set; }
    public bool Dismissed { get; set; }

    // Told by email already, for a reader who has reminders by email.
    public bool Mailed { get; set; }
}

public sealed record SeriesEntry(string Key, string Title, int? Year);

public sealed record SeriesAlertResponse(int Id, string Series, string Author, string Title, int? Year, DateTimeOffset FoundAt);

public interface ISeriesCatalog
{
    // The books a catalog lists in a series by an author, newest first; null when it could not be asked.
    Task<IReadOnlyList<SeriesEntry>?> FindAsync(string series, string author, CancellationToken cancellationToken);
}

public sealed class OpenLibrarySeries(HttpClient http) : ISeriesCatalog
{
    public async Task<IReadOnlyList<SeriesEntry>?> FindAsync(string series, string author, CancellationToken cancellationToken)
    {
        var query = $"series:\"{Quoted(series)}\" author:\"{Quoted(author)}\"";
        try
        {
            var response = await http.GetAsync("search.json?q=" + Uri.EscapeDataString(query) + "&fields=key,title,first_publish_year&sort=new&limit=40", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var found = await response.Content.ReadFromJsonAsync<Search>(cancellationToken);
            return (found?.Docs ?? [])
                .Where(doc => !string.IsNullOrWhiteSpace(doc.Key) && !string.IsNullOrWhiteSpace(doc.Title))
                .Select(doc => new SeriesEntry(doc.Key!, doc.Title!.Trim(), doc.FirstPublishYear is >= 1000 and <= 2100 ? doc.FirstPublishYear : null))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or Polly.ExecutionRejectedException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static string Quoted(string text) => text.Replace("\"", "", StringComparison.Ordinal).Replace("\\", "", StringComparison.Ordinal).Trim();

    private sealed class Search
    {
        public Doc[]? Docs { get; set; }
    }

    private sealed class Doc
    {
        public string? Key { get; set; }

        public string? Title { get; set; }

        [JsonPropertyName("first_publish_year")]
        public int? FirstPublishYear { get; set; }
    }
}

// Once a day, for each reader who asks for it, every series they have read or are reading is looked up, and a book
// in it that is newer than theirs and not on their shelf becomes an alert on the Series page. Only the series and
// author names leave the shelf.
public sealed partial class SeriesWatch(IServiceScopeFactory scopes, IConfiguration configuration, IEmailSender email, ILogger<SeriesWatch> logger) : BackgroundService
{
    public const int MaxSeriesPerCheck = 60;
    private const int NewestWithoutYear = 3;
    private static readonly TimeSpan Every = TimeSpan.FromDays(1);
    private readonly SemaphoreSlim checking = new(1, 1);

    // A pause between questions, to be kind to Open Library.
    private TimeSpan Pause => TimeSpan.FromMilliseconds(configuration.GetValue("SeriesAlerts:PauseMilliseconds", 1000));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("SeriesAlerts:Enabled", true))
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await CheckDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or HttpRequestException)
            {
                logger.LogWarning(ex, "Could not look for new books in series; it will try again.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task CheckDueAsync(CancellationToken cancellationToken)
    {
        List<int> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var since = DateTimeOffset.UtcNow - Every;
            due = (await db.Settings.AsNoTracking().Where(setting => setting.WatchSeries).Select(setting => new { setting.Id, setting.SeriesCheckedAt }).ToListAsync(cancellationToken))
                .Where(setting => setting.SeriesCheckedAt is null || setting.SeriesCheckedAt < since)
                .Select(setting => setting.Id)
                .ToList();
        }

        foreach (var readerId in due)
        {
            if (await CheckAsync(readerId, cancellationToken) is > 0)
            {
                await MailAsync(readerId, cancellationToken);
            }
        }
    }

    // New books in a reader's series, by email, when they have reminders by email: each book once.
    public async Task<bool> MailAsync(int readerId, CancellationToken cancellationToken)
    {
        if (!email.Enabled)
        {
            return false;
        }

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var reader = await db.Readers.AsNoTracking().FirstOrDefaultAsync(item => item.Id == readerId, cancellationToken);
        if (reader is not { EmailReminders: true, Email: { } address })
        {
            return false;
        }

        var news = await db.SeriesAlerts.Where(alert => !alert.Dismissed && !alert.Mailed).OrderBy(alert => alert.Series).ThenBy(alert => alert.Year).ToListAsync(cancellationToken);
        if (news.Count == 0)
        {
            return false;
        }

        EmailMessage message;
        using (Localization.Words.Speaking(reader.Language))
        {
            var site = EmailRules.PublicAddress(configuration, null);
            var lines = news.Select(alert => alert.Year is int year ? T("{0} ({1}), in {2}", alert.Title, year, alert.Series) : T("{0}, in {1}", alert.Title, alert.Series));
            var link = site.Length > 0 ? T("See them on Shelf: {0}", $"{site}/series") : T("Open Shelf and go to Series to see them.");
            var body = T("Hello {0},", reader.Name) + "\n\n" + T("There are new books in series you are reading:") + "\n\n" + string.Join('\n', lines.Select(line => "- " + line))
                + "\n\n" + link + "\n\n" + T("You can turn these emails off from your account, or series alerts off under Series.");
            message = new EmailMessage(address, news.Count == 1 ? T("A new book in your series") : T("{0} new books in your series", news.Count), body);
        }

        await email.SendAsync(message, cancellationToken);
        foreach (var alert in news)
        {
            alert.Mailed = true;
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Looks now for one reader. Returns how many new books were found, or null when another look is under way.
    public async Task<int?> CheckAsync(int readerId, CancellationToken cancellationToken)
    {
        if (!await checking.WaitAsync(0, cancellationToken))
        {
            return null;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var catalog = scope.ServiceProvider.GetRequiredService<ISeriesCatalog>();
            return await LookAsync(db, catalog, Pause, cancellationToken);
        }
        finally
        {
            checking.Release();
        }
    }

    internal static async Task<int> LookAsync(ShelfDb db, ISeriesCatalog catalog, TimeSpan pause, CancellationToken cancellationToken)
    {
        var books = await db.Books.AsNoTracking()
            .Select(book => new { book.Title, book.Author, book.Series, book.Year, book.Status })
            .ToListAsync(cancellationToken);
        var onShelf = books.Select(book => Simple(book.Title)).ToHashSet(StringComparer.Ordinal);
        var known = (await db.SeriesAlerts.Select(alert => alert.WorkKey).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);

        // A series is followed once a book in it is being read or has been finished.
        var followed = books
            .Where(book => !string.IsNullOrWhiteSpace(book.Series))
            .GroupBy(book => (Series: Simple(book.Series), Author: Simple(book.Author)))
            .Where(group => group.Any(book => book.Status is BookStatus.Reading or BookStatus.Finished))
            .Select(group => (Series: group.First().Series!.Trim(), Author: group.First().Author.Trim(), Latest: group.Max(book => book.Year)))
            .OrderBy(series => series.Series, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSeriesPerCheck)
            .ToList();

        var found = 0;
        foreach (var (series, author, latest) in followed)
        {
            // With no year on the reader's books to go by, only the newest few are offered.
            var entries = await catalog.FindAsync(series, author, cancellationToken) ?? [];
            foreach (var entry in latest is null ? entries.Take(NewestWithoutYear) : entries)
            {
                if (known.Contains(entry.Key) || onShelf.Contains(Simple(entry.Title)) || Gathered().IsMatch(entry.Title)
                    || (latest is int year && (entry.Year is null || entry.Year < year)))
                {
                    continue;
                }

                known.Add(entry.Key);
                onShelf.Add(Simple(entry.Title));
                db.SeriesAlerts.Add(new SeriesAlert
                {
                    OwnerId = db.ReaderId,
                    Series = series,
                    Author = author,
                    Title = entry.Title.Length > 200 ? entry.Title[..200] : entry.Title,
                    Year = entry.Year,
                    WorkKey = entry.Key,
                    FoundAt = DateTimeOffset.UtcNow,
                });
                found++;
            }

            if (pause > TimeSpan.Zero)
            {
                await Task.Delay(pause, cancellationToken);
            }
        }

        var setting = await db.Settings.FirstOrDefaultAsync(item => item.Id == db.ReaderId, cancellationToken);
        if (setting is null)
        {
            setting = new ShelfSetting { Id = db.ReaderId };
            db.Settings.Add(setting);
        }

        setting.SeriesCheckedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return found;
    }

    public static async Task<SeriesAlertResponse[]> ListAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        (await db.SeriesAlerts.AsNoTracking().Where(alert => !alert.Dismissed).ToListAsync(cancellationToken))
            .OrderByDescending(alert => alert.Year ?? 0)
            .ThenBy(alert => alert.Series, StringComparer.OrdinalIgnoreCase)
            .Select(alert => new SeriesAlertResponse(alert.Id, alert.Series, alert.Author, alert.Title, alert.Year, alert.FoundAt))
            .ToArray();

    public static Task<int> CountAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        db.SeriesAlerts.CountAsync(alert => !alert.Dismissed, cancellationToken);

    public static async Task<bool> DismissAsync(ShelfDb db, int id, CancellationToken cancellationToken = default) =>
        await db.SeriesAlerts.Where(alert => alert.Id == id).ExecuteUpdateAsync(set => set.SetProperty(alert => alert.Dismissed, true), cancellationToken) > 0;

    // Puts the book on the want list, in its series after the last one there, and lets the alert go.
    public static async Task<int?> WantAsync(ShelfDb db, int id, CancellationToken cancellationToken = default)
    {
        var alert = await db.SeriesAlerts.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (alert is null)
        {
            return null;
        }

        var series = Simple(alert.Series);
        var numbers = await db.Books.Where(book => book.Series != null && book.SeriesNumber != null).Select(book => new { book.Series, book.SeriesNumber }).ToListAsync(cancellationToken);
        var last = numbers.Where(book => Simple(book.Series) == series).Select(book => book.SeriesNumber).Max();
        var book = new Book
        {
            Title = alert.Title,
            Author = alert.Author,
            Series = alert.Series,
            SeriesNumber = last is int number && number < 999 ? number + 1 : null,
            Year = alert.Year,
            Status = BookStatus.Want,
            AddedAt = DateTimeOffset.UtcNow,
        };
        db.Books.Add(book);
        alert.Dismissed = true;
        await db.SaveChangesAsync(cancellationToken);
        return book.Id;
    }

    public static async Task<(bool On, DateTimeOffset? CheckedAt)> GetAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var setting = await db.Settings.AsNoTracking().FirstOrDefaultAsync(item => item.Id == db.ReaderId, cancellationToken);
        return (setting?.WatchSeries == true, setting?.SeriesCheckedAt);
    }

    public static async Task SetAsync(ShelfDb db, bool on, CancellationToken cancellationToken = default)
    {
        var setting = await db.Settings.FirstOrDefaultAsync(item => item.Id == db.ReaderId, cancellationToken);
        if (setting is null)
        {
            setting = new ShelfSetting { Id = db.ReaderId };
            db.Settings.Add(setting);
        }

        setting.WatchSeries = on;
        await db.SaveChangesAsync(cancellationToken);
        await Audit.NoteAsync(db, on ? Say("Turned on series alerts") : Say("Turned off series alerts"), cancellationToken: cancellationToken);
    }

    private static string Simple(string? text) => Letters().Replace((text ?? "").ToLowerInvariant(), "");

    // Box sets, collections, and numbered volumes of a series: not a new book in it.
    [GeneratedRegex(@"\b(box(ed)?\s*set|collection|omnibus|trilogy|books?\s*\d+\s*(-|–|to|&|and)\s*\d+|vol(ume)?\.?\s*\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Gathered();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex Letters();
}

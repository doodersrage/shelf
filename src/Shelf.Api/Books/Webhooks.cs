using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public enum WebhookFormat
{
    // Shelf's own JSON, signed: for Home Assistant, n8n, Node-RED, and scripts.
    Json,

    // A line of text, as Discord's webhooks take it.
    Discord,

    // A line of text, as Slack's (and Mattermost's) incoming webhooks take it.
    Slack,
}

// A reader's own webhook: an address Shelf posts to when something happens to their books, loans, clubs, or series.
public sealed class Webhook
{
    public const int MaxPerReader = 10;

    public int Id { get; set; }
    public int ReaderId { get; set; }
    public required string Url { get; set; }
    public WebhookFormat Format { get; set; }

    // The events it is sent, by kind and separated by commas; empty for all of them.
    public string Events { get; set; } = "";

    // The secret each JSON body is signed with, sealed with the shelf's keys.
    public required string Secret { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSentAt { get; set; }
    public int? LastStatus { get; set; }

    // Failures in a row; past MaxFailures the webhook is turned off until the reader turns it on again.
    public int Failures { get; set; }
}

public sealed record WebhookSummary(int Id, string Url, WebhookFormat Format, string[] Events, bool Enabled, DateTimeOffset CreatedAt, DateTimeOffset? LastSentAt, int? LastStatus, int Failures);

public sealed record NewWebhook(WebhookSummary Webhook, string Secret);

public interface IWebhookPoster
{
    // The status the address answered with, or null when it could not be reached.
    Task<int?> PostAsync(string url, string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken);
}

public sealed class WebhookPoster(HttpClient http) : IWebhookPoster
{
    public async Task<int?> PostAsync(string url, string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        try
        {
            using var answer = await http.SendAsync(request, cancellationToken);
            return (int)answer.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }
}

public sealed class Webhooks(IServiceScopeFactory scopes, IWebhookPoster poster, IDataProtectionProvider protection, IConfiguration configuration) : IShelfEventHandler
{
    public const int MaxFailures = 20;
    public const string TestKind = "webhook.test";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    private IDataProtector Protector => protection.CreateProtector("Shelf.Webhooks.Secret");

    public bool Enabled => configuration.GetValue("Webhooks:Enabled", true);

    public static async Task<WebhookSummary[]> ListAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        (await db.Webhooks.AsNoTracking().Where(item => item.ReaderId == db.ReaderId).ToListAsync(cancellationToken))
            .OrderBy(item => item.CreatedAt).Select(Summary).ToArray();

    public async Task<(NewWebhook? Made, string? Problem)> AddAsync(ShelfDb db, string? url, WebhookFormat format, IEnumerable<string>? events, CancellationToken cancellationToken = default)
    {
        if (await Family.IsChildAsync(db, cancellationToken: cancellationToken))
        {
            return (null, T("A child's account cannot do that. Ask the grown-up who made it."));
        }

        if (!Notices.IsWebAddress(url, out var address) || address.AbsoluteUri.Length > 1000)
        {
            return (null, T("Give the webhook's whole address, starting with https:// or http://."));
        }

        if (await db.Webhooks.CountAsync(item => item.ReaderId == db.ReaderId, cancellationToken) >= Webhook.MaxPerReader)
        {
            return (null, T("You have {0} webhooks, the most there can be. Remove one first.", Webhook.MaxPerReader));
        }

        var secret = "whsec_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var hook = new Webhook
        {
            ReaderId = db.ReaderId,
            Url = address.AbsoluteUri,
            Format = format,
            Events = Clean(events),
            Secret = Protector.Protect(secret),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Webhooks.Add(hook);
        await db.SaveChangesAsync(cancellationToken);
        await Audit.NoteAsync(db, Say("Added a webhook"), await db.Readers.FirstAsync(item => item.Id == db.ReaderId, cancellationToken), address.Host, cancellationToken);
        return (new NewWebhook(Summary(hook), secret), null);
    }

    public static async Task<bool> SetAsync(ShelfDb db, int id, bool enabled, IEnumerable<string>? events = null, CancellationToken cancellationToken = default)
    {
        var hook = await db.Webhooks.FirstOrDefaultAsync(item => item.Id == id && item.ReaderId == db.ReaderId, cancellationToken);
        if (hook is null)
        {
            return false;
        }

        hook.Enabled = enabled;
        hook.Failures = enabled ? 0 : hook.Failures;
        hook.Events = events is null ? hook.Events : Clean(events);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public static async Task RemoveAsync(ShelfDb db, int id, CancellationToken cancellationToken = default) =>
        await db.Webhooks.Where(item => item.Id == id && item.ReaderId == db.ReaderId).ExecuteDeleteAsync(cancellationToken);

    // A test event to one webhook, now; returns the status it answered with.
    public async Task<int?> TestAsync(ShelfDb db, int id, CancellationToken cancellationToken = default)
    {
        var hook = await db.Webhooks.FirstOrDefaultAsync(item => item.Id == id && item.ReaderId == db.ReaderId, cancellationToken);
        if (hook is null)
        {
            return null;
        }

        var reader = await db.Readers.AsNoTracking().FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        var payload = new WebhookPayload(TestKind, DateTimeOffset.UtcNow, new WebhookPerson(reader.Id, reader.Name), null, null, null, null, Say("A test from Shelf."));
        return await DeliverAsync(db, hook, payload, reader.Language, cancellationToken);
    }

    public async Task HandleAsync(ShelfEvent shelfEvent, CancellationToken cancellationToken)
    {
        // Only the kinds a webhook can ask for; what happens to an account stays between Shelf and its reader.
        if (!EventKinds.All.Contains(shelfEvent.Kind))
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var book = shelfEvent.BookId is int bookId
            ? await db.Books.IgnoreQueryFilters().AsNoTracking().Where(item => item.Id == bookId)
                .Select(item => new { item.Id, item.Title, item.Author, item.Status, item.OwnerId, item.Isbn }).FirstOrDefaultAsync(cancellationToken)
            : null;

        // Whose webhooks hear of it: the book's owner, the other reader in a loan, the members of a club, the reader of a series.
        var concerned = new HashSet<int>();
        int? clubId = shelfEvent.ClubId;
        string? text = null;
        if (book?.OwnerId is int owner)
        {
            concerned.Add(owner);
        }

        if (shelfEvent.Kind is EventKinds.BookLent or EventKinds.BookReturned or EventKinds.SeriesFound or EventKinds.ClubJoined && shelfEvent.ReaderId is int other)
        {
            concerned.Add(other);
        }

        if (shelfEvent.Kind == EventKinds.ClubPosted && shelfEvent.PostId is int postId)
        {
            var post = await db.ClubPosts.AsNoTracking().Where(item => item.Id == postId).Select(item => new { item.Book!.ClubId, item.Text, item.Spoiler }).FirstOrDefaultAsync(cancellationToken);
            (clubId, text) = (post?.ClubId, post?.Spoiler == true ? T("(a spoiler, in the club)") : post?.Text);
        }

        if (shelfEvent.Kind == EventKinds.ClubMeeting && shelfEvent.MeetingId is int meetingId)
        {
            var meeting = await db.ClubMeetings.AsNoTracking().Where(item => item.Id == meetingId).Select(item => new { item.At, item.TimeZone, Title = item.Book!.Title }).FirstOrDefaultAsync(cancellationToken);
            text = meeting is null ? null : $"{meeting.Title} · {ClubMeetings.Say(meeting.At, meeting.TimeZone)}";
        }

        if (shelfEvent.Kind is EventKinds.ClubPosted or EventKinds.ClubMeeting && clubId is int postedIn)
        {
            concerned.UnionWith(await db.ClubMembers.AsNoTracking().Where(item => item.ClubId == postedIn).Select(item => item.ReaderId).ToListAsync(cancellationToken));
        }

        if (concerned.Count == 0)
        {
            return;
        }

        var names = await db.Readers.AsNoTracking()
            .Where(item => item.Id == shelfEvent.ActorId || item.Id == shelfEvent.ReaderId)
            .Select(item => new { item.Id, item.Name, item.Language }).ToListAsync(cancellationToken);
        WebhookPerson? Person(int? id) => names.FirstOrDefault(item => item.Id == id) is { } found ? new WebhookPerson(found.Id, found.Name) : null;
        var club = clubId is int someClub ? await db.Clubs.AsNoTracking().Where(item => item.Id == someClub).Select(item => new WebhookClub(item.Id, item.Name)).FirstOrDefaultAsync(cancellationToken) : null;
        var site = EmailRules.PublicAddress(configuration, null);
        var payload = new WebhookPayload(
            shelfEvent.Kind,
            shelfEvent.At,
            Person(shelfEvent.ActorId),
            book is null ? null : new WebhookBook(book.Id, book.Title, book.Author, book.Status, book.Isbn, site.Length > 0 ? $"{site}/library/{book.Id}" : null),
            Person(shelfEvent.ReaderId),
            club,
            shelfEvent.Kind == EventKinds.SeriesFound ? shelfEvent.Count : null,
            text);

        // A book's title goes only to its owner and the other reader in a loan; club members see the club.
        WebhookPayload For(int reader) => reader == book?.OwnerId || shelfEvent.Kind is EventKinds.BookLent or EventKinds.BookReturned ? payload : payload with { Book = null };

        // Every reader concerned keeps it in their feed, webhooks or not, as they'd have been sent it.
        await EventFeed.RecordAsync(db, concerned.ToDictionary(reader => reader, reader => JsonSerializer.Serialize(For(reader), Json)), shelfEvent.Kind, cancellationToken);
        if (!Enabled)
        {
            return;
        }

        var hooks = await db.Webhooks.Where(item => item.Enabled && concerned.Contains(item.ReaderId)).ToListAsync(cancellationToken);
        hooks = hooks.Where(hook => hook.Events.Length == 0 || hook.Events.Split(',').Contains(shelfEvent.Kind)).ToList();
        if (hooks.Count == 0)
        {
            return;
        }

        var languages = await db.Readers.AsNoTracking().Where(item => concerned.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.Language, cancellationToken);
        foreach (var hook in hooks)
        {
            await DeliverAsync(db, hook, For(hook.ReaderId), languages.GetValueOrDefault(hook.ReaderId), cancellationToken);
        }
    }

    private async Task<int?> DeliverAsync(ShelfDb db, Webhook hook, WebhookPayload payload, string? language, CancellationToken cancellationToken)
    {
        string body;
        using (Speaking(language))
        {
            body = hook.Format switch
            {
                WebhookFormat.Discord => JsonSerializer.Serialize(new { content = Line(payload), username = "Shelf" }),
                WebhookFormat.Slack => JsonSerializer.Serialize(new { text = Line(payload) }),
                _ => JsonSerializer.Serialize(payload, Json),
            };
        }

        string secret;
        try
        {
            secret = Protector.Unprotect(hook.Secret);
        }
        catch (CryptographicException)
        {
            secret = "";
        }

        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = $"Shelf/{ShelfVersion.Response.Version} (webhook)",
            ["X-Shelf-Event"] = payload.Event,
            ["X-Shelf-Signature"] = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))),
        };
        var status = await poster.PostAsync(hook.Url, body, headers, cancellationToken);
        var ok = status is >= 200 and < 300;
        hook.LastSentAt = DateTimeOffset.UtcNow;
        hook.LastStatus = status;
        hook.Failures = ok ? 0 : hook.Failures + 1;
        hook.Enabled = hook.Failures < MaxFailures && hook.Enabled;
        await db.SaveChangesAsync(cancellationToken);
        return status;
    }

    // The event as a sentence, for chat apps.
    public static string Line(WebhookPayload payload)
    {
        var who = payload.Actor?.Name ?? "";
        var title = payload.Book is { } book ? $"{book.Title} ({book.Author})" : "";
        return payload.Event switch
        {
            EventKinds.BookAdded => T("{0} added {1} to their shelf.", who, title),
            EventKinds.BookStarted => T("{0} started reading {1}.", who, title),
            EventKinds.BookFinished => T("{0} finished {1}.", who, title),
            EventKinds.BookLent => T("{0} lent {1} to {2}.", who, title, payload.Reader?.Name ?? ""),
            EventKinds.BookReturned => T("{0} was returned by {1}.", title, payload.Reader?.Name ?? ""),
            EventKinds.LoanAsked => T("{0} asked to borrow {1}.", payload.Reader?.Name ?? "", title),
            EventKinds.ClubPosted => T("{0} in {1}: {2}", who, payload.Club?.Name ?? "", payload.Text ?? ""),
            EventKinds.ClubJoined => T("{0} joined {1}.", payload.Reader?.Name ?? "", payload.Club?.Name ?? ""),
            EventKinds.SeriesFound => payload.Count == 1 ? T("A new book in your series") : T("{0} new books in your series", payload.Count ?? 0),
            EventKinds.BookReread => T("{0} is reading {1} again.", who, title),
            EventKinds.ClubMeeting => T("{0} arranged a meeting of {1}: {2}", who, payload.Club?.Name ?? "", payload.Text ?? ""),
            _ => T(payload.Text ?? "A test from Shelf."),
        };
    }

    private static string Clean(IEnumerable<string>? events) =>
        string.Join(',', (events ?? []).Where(kind => EventKinds.All.Contains(kind)).Distinct().Order(StringComparer.Ordinal)) is var list && list.Split(',').Length == EventKinds.All.Count ? "" : list;

    private static WebhookSummary Summary(Webhook hook) =>
        new(hook.Id, hook.Url, hook.Format, hook.Events.Length == 0 ? [.. EventKinds.All] : hook.Events.Split(','), hook.Enabled, hook.CreatedAt, hook.LastSentAt, hook.LastStatus, hook.Failures);
}

// What Shelf sends a JSON webhook.
public sealed record WebhookPayload(string Event, DateTimeOffset At, WebhookPerson? Actor, WebhookBook? Book, WebhookPerson? Reader, WebhookClub? Club, int? Count, string? Text);

public sealed record WebhookPerson(int Id, string Name);

public sealed record WebhookBook(int Id, string Title, string Author, BookStatus Status, string? Isbn, string? Url);

public sealed record WebhookClub(int Id, string Name);

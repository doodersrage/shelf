using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Shelf.Api.Books;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Readers;

// Who may make an account: anyone, only someone invited, or no one new. Signing up as the shelf's first reader is
// always open.
public enum SignUpMode
{
    Open,
    Invites,
    Closed,
}

// The shelf's own settings, as against a reader's: one row, made when first changed.
public sealed class SiteSetting
{
    public const int MaxAnnouncementLength = 1000;

    public int Id { get; set; }

    // Null follows Accounts:AllowSignUp.
    public SignUpMode? SignUp { get; set; }

    // A note shown at the top of every page until an admin takes it down, and when it went up.
    public string? Announcement { get; set; }
    public DateTimeOffset? AnnouncedAt { get; set; }
}

// An invitation to make an account, sent to one address: only the hash of its link's token is kept, it lasts a week,
// and it works once. Whoever opens it signs up with that address, already confirmed, even when sign-up is closed.
public sealed class Invitation
{
    public int Id { get; set; }
    public required string Email { get; set; }
    public required string NormalizedEmail { get; set; }
    public required string TokenHash { get; set; }
    public int InvitedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public int? ReaderId { get; set; }
}

public sealed record InvitationSummary(int Id, string Email, string InvitedBy, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? AcceptedAt, string? Reader);

// What came of inviting one address: its link (shown to the admin once, to pass on by hand when email cannot), whether
// it went by email, or why not.
public sealed record InvitationMade(string Email, string? Link, bool Sent, string? Problem);

public sealed record ReaderRow(
    int Id,
    string Name,
    string? Email,
    string? PendingEmail,
    bool IsAdmin,
    bool Child,
    bool Suspended,
    bool TwoStep,
    int Books,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt);

public sealed record InvitationRequest(string? Emails);

public sealed record SuspendRequest(bool Suspended);

public sealed record RenameRequest(string? Name);

public sealed record SignUpModeRequest(SignUpMode Mode);

public sealed record AnnouncementRequest(string? Text, bool Email = false);

public sealed record Announcement(string Text, DateTimeOffset At);

// A job of the server's own an admin can run now, by its key; Beat names its heartbeat on Server health, if it has one.
public sealed record AdminJob(string Key, string Label, string? Beat);

public static class Management
{
    public const int MaxInvitesAtOnce = 50;
    public static readonly TimeSpan InviteLasts = TimeSpan.FromDays(7);
    private const string AnnouncementKey = "shelf:announcement";

    private static async Task<SiteSetting> SiteAsync(ShelfDb db, CancellationToken cancellationToken)
    {
        if (await db.SiteSettings.FirstOrDefaultAsync(cancellationToken) is { } site)
        {
            return site;
        }

        site = new SiteSetting { Id = 1 };
        db.SiteSettings.Add(site);
        return site;
    }

    public static async Task<SignUpMode> SignUpModeAsync(ShelfDb db, IConfiguration configuration, CancellationToken cancellationToken = default) =>
        await db.SiteSettings.AsNoTracking().Select(site => site.SignUp).FirstOrDefaultAsync(cancellationToken)
            ?? (AccountEndpoints.SignUpOpen(configuration) ? SignUpMode.Open : SignUpMode.Closed);

    public static async Task SetSignUpModeAsync(ShelfDb db, SignUpMode mode, CancellationToken cancellationToken = default)
    {
        var site = await SiteAsync(db, cancellationToken);
        var changed = site.SignUp != mode;
        site.SignUp = mode;
        await db.SaveChangesAsync(cancellationToken);
        if (changed)
        {
            await Audit.NoteAsync(db, Say("Changed who may sign up"), detail: mode.ToString(), cancellationToken: cancellationToken);
        }
    }

    // Every reader but the sandboxes, with what an admin looks for: their address and whether it is confirmed, their
    // standing, two-step sign-in, books, and when they were last seen on any device.
    public static async Task<ReaderRow[]> ReadersAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var counts = await db.Books.IgnoreQueryFilters()
            .Where(book => book.OwnerId != null)
            .GroupBy(book => book.OwnerId!.Value)
            .Select(group => new { Id = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Id, item => item.Count, cancellationToken);

        // SQLite cannot take the latest of times with an offset, so the sessions are gone through here.
        var seen = (await db.ReaderSessions.AsNoTracking().Select(session => new { session.ReaderId, session.LastSeenAt }).ToListAsync(cancellationToken))
            .GroupBy(session => session.ReaderId)
            .ToDictionary(group => group.Key, group => group.Max(session => session.LastSeenAt));
        var readers = await db.Readers.AsNoTracking().Where(reader => reader.SandboxOf == null).ToListAsync(cancellationToken);
        return readers
            .OrderBy(reader => reader.Name, StringComparer.OrdinalIgnoreCase)
            .Select(reader => new ReaderRow(reader.Id, reader.Name, reader.Email, reader.PendingEmail, reader.IsAdmin, reader.Restricted, reader.Suspended,
                reader.TwoFactorEnabled, counts.GetValueOrDefault(reader.Id), reader.CreatedAt, seen.TryGetValue(reader.Id, out var at) ? at : null))
            .ToArray();
    }

    // A suspended reader cannot sign in, by any way, and is signed out everywhere; their shelf stays as it is.
    public static async Task<AccountProblem?> SuspendAsync(ShelfDb db, int readerId, bool suspended, CancellationToken cancellationToken = default)
    {
        var reader = await db.Readers.FirstOrDefaultAsync(item => item.Id == readerId && item.SandboxOf == null, cancellationToken);
        if (reader is null)
        {
            return AccountProblem.NoSuchReader;
        }

        if (readerId == db.ReaderId)
        {
            return AccountProblem.NotYourself;
        }

        if (reader.Suspended == suspended)
        {
            return null;
        }

        reader.Suspended = suspended;
        if (suspended)
        {
            reader.Stamp = ReaderRules.NewStamp();
        }

        await db.SaveChangesAsync(cancellationToken);
        if (suspended)
        {
            await db.ReaderSessions.Where(session => session.ReaderId == readerId).ExecuteDeleteAsync(cancellationToken);
        }

        await Audit.NoteAsync(db, suspended ? Say("Suspended an account") : Say("Restored a suspended account"), reader, cancellationToken: cancellationToken);
        return null;
    }

    // Every device the reader is signed in on is signed out; their password stays as it is.
    public static async Task<bool> SignOutEverywhereAsync(ShelfDb db, int readerId, CancellationToken cancellationToken = default)
    {
        var reader = await db.Readers.FirstOrDefaultAsync(item => item.Id == readerId, cancellationToken);
        if (reader is null)
        {
            return false;
        }

        reader.Stamp = ReaderRules.NewStamp();
        await db.SaveChangesAsync(cancellationToken);
        await db.ReaderSessions.Where(session => session.ReaderId == readerId).ExecuteDeleteAsync(cancellationToken);
        await Audit.NoteAsync(db, Say("Signed out of every device"), reader, cancellationToken: cancellationToken);
        return true;
    }

    // A reader's name, as an admin changes it. A reader who signs in by name keeps one no one else has.
    public static async Task<AccountProblem?> RenameAsync(ShelfDb db, int readerId, string? name, CancellationToken cancellationToken = default)
    {
        if (ReaderRules.NameProblem(name) is { } problem)
        {
            return problem;
        }

        var reader = await db.Readers.FirstOrDefaultAsync(item => item.Id == readerId && item.SandboxOf == null, cancellationToken);
        if (reader is null)
        {
            return AccountProblem.NoSuchReader;
        }

        var normalized = ReaderRules.Normalize(name!);
        if (reader.NormalizedEmail is null && reader.PendingNormalizedEmail is null
            && await db.Readers.AnyAsync(other => other.Id != readerId && other.NormalizedName == normalized && other.SandboxOf == null, cancellationToken))
        {
            return AccountProblem.NameTaken;
        }

        var before = reader.Name;
        (reader.Name, reader.NormalizedName) = (ReaderRules.Clean(name), normalized);
        await db.SaveChangesAsync(cancellationToken);
        if (before != reader.Name)
        {
            await Audit.NoteAsync(db, Say("Renamed an account"), reader, before, cancellationToken);
        }

        return null;
    }

    // Invitations to the addresses given, separated by commas, semicolons, spaces, or lines: up to fifty at once. An
    // address someone already signs in with is passed over; one already invited is invited afresh.
    public static async Task<InvitationMade[]> InviteAsync(ShelfDb db, IEmailSender email, string? addresses, string linkAddress, CancellationToken cancellationToken = default)
    {
        var wanted = (addresses ?? "").Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxInvitesAtOnce)
            .ToList();
        var made = new List<InvitationMade>();
        foreach (var address in wanted)
        {
            if (EmailRules.CleanAddress(address) is not { Length: > 0 } clean)
            {
                made.Add(new InvitationMade(address, null, false, ReaderRules.Describe(AccountProblem.EmailInvalid)));
                continue;
            }

            var normalized = ReaderRules.NormalizeEmail(clean);
            if (await db.Readers.AnyAsync(reader => reader.NormalizedEmail == normalized, cancellationToken))
            {
                made.Add(new InvitationMade(clean, null, false, ReaderRules.Describe(AccountProblem.EmailTaken)));
                continue;
            }

            await db.Invitations.Where(item => item.NormalizedEmail == normalized && item.AcceptedAt == null).ExecuteDeleteAsync(cancellationToken);
            var invitation = new Invitation { Email = clean, NormalizedEmail = normalized, TokenHash = "", InvitedById = db.ReaderId, CreatedAt = DateTimeOffset.UtcNow };
            db.Invitations.Add(invitation);
            made.Add(await SendAsync(db, email, invitation, linkAddress, cancellationToken));
            await Audit.NoteAsync(db, Say("Invited someone to the shelf"), detail: clean, cancellationToken: cancellationToken);
        }

        return [.. made];
    }

    // The invitation again, with a new link: the one sent before no longer works.
    public static async Task<InvitationMade?> ResendAsync(ShelfDb db, IEmailSender email, int id, string linkAddress, CancellationToken cancellationToken = default)
    {
        var invitation = await db.Invitations.FirstOrDefaultAsync(item => item.Id == id && item.AcceptedAt == null, cancellationToken);
        return invitation is null ? null : await SendAsync(db, email, invitation, linkAddress, cancellationToken);
    }

    private static async Task<InvitationMade> SendAsync(ShelfDb db, IEmailSender email, Invitation invitation, string linkAddress, CancellationToken cancellationToken)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        (invitation.TokenHash, invitation.ExpiresAt) = (Hash(token), DateTimeOffset.UtcNow + InviteLasts);
        await db.SaveChangesAsync(cancellationToken);
        var link = $"{linkAddress}/signup?invite={token}";
        if (!email.Enabled)
        {
            return new InvitationMade(invitation.Email, link, false, null);
        }

        var inviter = await db.Readers.AsNoTracking().Where(reader => reader.Id == invitation.InvitedById).Select(reader => reader.Name).FirstOrDefaultAsync(cancellationToken) ?? "";
        try
        {
            await email.SendAsync(new EmailMessage(
                invitation.Email,
                T("You're invited to Shelf"),
                T("Hello,\n\n{0} invites you to their shelf on Shelf, to keep track of your books, read and listen, and lend to the others there.\n\n{1}\n\nThe invitation lasts a week. If you did not expect it, ignore this email.", inviter, link),
                Button: T("Make your account")), cancellationToken);
            return new InvitationMade(invitation.Email, link, true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InvitationMade(invitation.Email, link, false, T("The mail server did not take it: {0}", ex.Message));
        }
    }

    public static async Task<InvitationSummary[]> InvitationsAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var invitations = await db.Invitations.AsNoTracking().ToListAsync(cancellationToken);
        var ids = invitations.Select(item => item.InvitedById).Concat(invitations.Where(item => item.ReaderId is not null).Select(item => item.ReaderId!.Value)).Distinct().ToList();
        var names = await db.Readers.AsNoTracking().Where(reader => ids.Contains(reader.Id)).ToDictionaryAsync(reader => reader.Id, reader => reader.Name, cancellationToken);
        return invitations
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new InvitationSummary(item.Id, item.Email, names.GetValueOrDefault(item.InvitedById, ""), item.CreatedAt, item.ExpiresAt, item.AcceptedAt,
                item.ReaderId is int reader ? names.GetValueOrDefault(reader) : null))
            .ToArray();
    }

    public static async Task<bool> RevokeAsync(ShelfDb db, int id, CancellationToken cancellationToken = default) =>
        await db.Invitations.Where(item => item.Id == id && item.AcceptedAt == null).ExecuteDeleteAsync(cancellationToken) > 0;

    // The invitation a sign-up link carries, while it is still good.
    public static async Task<Invitation?> InvitationAsync(ShelfDb db, string? token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            return null;
        }

        var hash = Hash(token.Trim());
        var invitation = await db.Invitations.FirstOrDefaultAsync(item => item.TokenHash == hash && item.AcceptedAt == null, cancellationToken);
        return invitation is not null && invitation.ExpiresAt > DateTimeOffset.UtcNow ? invitation : null;
    }

    public static async Task AcceptAsync(ShelfDb db, Invitation invitation, Reader reader, CancellationToken cancellationToken = default)
    {
        (invitation.AcceptedAt, invitation.ReaderId) = (DateTimeOffset.UtcNow, reader.Id);
        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task<Announcement?> AnnouncementAsync(ShelfDb db, IMemoryCache cache, CancellationToken cancellationToken = default) =>
        await cache.GetOrCreateAsync(AnnouncementKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            var site = await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
            return site is { Announcement: { Length: > 0 } text, AnnouncedAt: { } at } ? new Announcement(text, at) : null;
        });

    // A note for every reader at the top of each page, and by email too when asked; empty takes it down. Returns how
    // many emails went.
    public static async Task<int> AnnounceAsync(ShelfDb db, IMemoryCache cache, IEmailSender email, string? text, bool byEmail, string site, CancellationToken cancellationToken = default)
    {
        var note = string.IsNullOrWhiteSpace(text) ? null : text.Trim()[..Math.Min(text.Trim().Length, SiteSetting.MaxAnnouncementLength)];
        var setting = await SiteAsync(db, cancellationToken);
        (setting.Announcement, setting.AnnouncedAt) = (note, note is null ? null : DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        cache.Remove(AnnouncementKey);
        await Audit.NoteAsync(db, note is null ? Say("Took down the announcement") : Say("Made an announcement"), cancellationToken: cancellationToken);
        if (note is null || !byEmail || !email.Enabled)
        {
            return 0;
        }

        var sent = 0;
        var readers = await db.Readers.AsNoTracking().Where(reader => reader.Email != null && reader.NormalizedEmail != null && !reader.Suspended && reader.SandboxOf == null)
            .Select(reader => new { reader.Email, reader.Name, reader.Language }).ToListAsync(cancellationToken);
        foreach (var reader in readers)
        {
            EmailMessage message;
            using (Speaking(reader.Language))
            {
                message = new EmailMessage(reader.Email!, T("News from your shelf"),
                    T("Hello {0},", reader.Name) + "\n\n" + note + (site.Length > 0 ? "\n\n" + T("Open Shelf: {0}", site) : ""));
            }

            try
            {
                await email.SendAsync(message, cancellationToken);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The sender logged it and Server health shows it; the rest still go.
            }
        }

        return sent;
    }

    // The jobs an admin can run straight away, rather than wait for their turn.
    public static IReadOnlyList<AdminJob> Jobs =>
    [
        new("notices", Say("Notifications: loans due, meeting reminders, and Server health"), nameof(Notices)),
        new("series", Say("New books in readers' series"), nameof(SeriesWatch)),
        new("files", Say("Check the stored files"), nameof(FileChecker)),
        new("podcasts", Say("Look for new podcast episodes"), null),
        new("audiobookshelf", Say("Audiobookshelf libraries kept in step"), null),
        new("readwise", Say("Highlights to Readwise"), null),
        new("hardcover", Say("Changes to Hardcover"), null),
        new("release", Say("Look for a newer Shelf"), nameof(ReleaseWatch)),
    ];

    public static async Task<bool> RunAsync(IServiceProvider services, string key, CancellationToken cancellationToken = default)
    {
        switch (key)
        {
            case "notices":
                var notices = services.GetRequiredService<Notices>();
                await notices.RemindMeetingsAsync(cancellationToken);
                await notices.RemindDueAsync(cancellationToken);
                await notices.TellHealthAsync(cancellationToken);
                Heartbeats.Beat(nameof(Notices));
                return true;
            case "series":
                var watch = services.GetRequiredService<SeriesWatch>();
                await using (var scope = services.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
                    foreach (var readerId in await db.Settings.AsNoTracking().Where(setting => setting.WatchSeries).Select(setting => setting.Id).ToListAsync(cancellationToken))
                    {
                        if (await watch.CheckAsync(readerId, cancellationToken) is > 0)
                        {
                            await watch.MailAsync(readerId, cancellationToken);
                        }
                    }
                }

                Heartbeats.Beat(nameof(SeriesWatch));
                return true;
            case "files":
                await using (var scope = services.CreateAsyncScope())
                {
                    await FileCheck.RunAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), services.GetRequiredService<EbookStore>(), services.GetRequiredService<AudioStore>(), services.GetRequiredService<CoverStore>(), cancellationToken);
                }

                Heartbeats.Beat(nameof(FileChecker));
                return true;
            case "podcasts":
                await services.GetRequiredService<PodcastService>().RefreshDueAsync(cancellationToken);
                return true;
            case "audiobookshelf":
                await services.GetRequiredService<AbsKeeper>().LookAsync(cancellationToken);
                return true;
            case "readwise":
                await services.GetRequiredService<ReadwiseSync>().SendDueAsync(cancellationToken);
                return true;
            case "hardcover":
                await services.GetRequiredService<HardcoverSync>().SendDueAsync(cancellationToken);
                return true;
            case "release":
                await services.GetRequiredService<ReleaseWatch>().CheckAsync(cancellationToken);
                Heartbeats.Beat(nameof(ReleaseWatch));
                return true;
            default:
                return false;
        }
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}

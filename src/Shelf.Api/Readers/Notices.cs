using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Books;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Readers;

// What a reader is told about: their loans (lent to them, returned, due, or overdue), asks to borrow their books,
// new books in their series, their book clubs, and, for an admin, new readers and problems on Server health.
[Flags]
public enum NoticeKinds
{
    None = 0,
    Loans = 1,
    Asks = 2,
    Series = 4,
    Clubs = 8,
    Admin = 16,
    All = Loans | Asks | Series | Clubs | Admin,
}

// A browser that asked to be told things, by the Push API: where its push service takes messages for it, and the
// keys that seal each message so only that browser can read it.
public sealed class PushSubscriptionRow
{
    public int Id { get; set; }
    public int ReaderId { get; set; }
    public required string Endpoint { get; set; }
    public required string P256dh { get; set; }
    public required string Auth { get; set; }
    public string? Label { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// One notification: a title, a line or two, and the page of Shelf it opens. A newer one with the same tag replaces it.
public sealed record Notice(string Title, string Body, string Url, string Tag);

public enum PushOutcome
{
    Sent,
    Gone,
    Failed,
}

public sealed record PushDevice(int Id, string? Label, DateTimeOffset CreatedAt);

public sealed record NoticeSettings(NoticeKinds Kinds, string? NtfyUrl, bool NtfyToken, string? GotifyUrl, bool GotifyToken, PushDevice[] Devices, int DueSoonDays = Reader.DefaultDueSoonDays, string? Email = null, bool EmailNotices = false);

public interface INoticeChannels
{
    // The key browsers check push messages against (VAPID), in URL-safe base64.
    string PushKey { get; }

    Task<bool> NtfyAsync(string url, string? token, Notice notice, string? link, CancellationToken cancellationToken);

    Task<bool> GotifyAsync(string url, string token, Notice notice, string? link, CancellationToken cancellationToken);

    Task<PushOutcome> PushAsync(PushSubscriptionRow subscription, Notice notice, CancellationToken cancellationToken);
}

// The shelf's own key pair for Web Push (VAPID): made once, and kept beside the keys that seal sign-ins, sealed by them.
public sealed class WebPushKeys
{
    private readonly ECParameters _parameters;

    public WebPushKeys(string folder, IDataProtectionProvider protection)
    {
        var protector = protection.CreateProtector("Shelf.WebPush.Key");
        var path = Path.Combine(folder, "web-push.key");
        byte[]? d = null;
        if (File.Exists(path))
        {
            try
            {
                d = protector.Unprotect(File.ReadAllBytes(path));
            }
            catch (CryptographicException)
            {
                // Sealed by keys that are gone: browsers subscribed with the old key subscribe again.
            }
        }

        using var key = d is null ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = d });
        _parameters = key.ExportParameters(includePrivateParameters: true);
        if (d is null)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(path, protector.Protect(_parameters.D!));
        }

        PublicKey = WebEncoders.Base64UrlEncode([0x04, .. _parameters.Q.X!, .. _parameters.Q.Y!]);
    }

    public string PublicKey { get; }

    // The signed token a push service checks: who sends, to which service, until when.
    public string Token(string audience, string subject, DateTimeOffset expires)
    {
        var header = WebEncoders.Base64UrlEncode("""{"typ":"JWT","alg":"ES256"}"""u8);
        var claims = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { aud = audience, exp = expires.ToUnixTimeSeconds(), sub = subject }));
        using var key = ECDsa.Create(_parameters);
        var signature = key.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{header}.{claims}.{WebEncoders.Base64UrlEncode(signature)}";
    }
}

public sealed class NoticeChannels(HttpClient http, WebPushKeys keys, IConfiguration configuration) : INoticeChannels
{
    public string PushKey => keys.PublicKey;

    // ntfy takes JSON at the server's root, naming the topic, which keeps any language in the title intact.
    public async Task<bool> NtfyAsync(string url, string? token, Notice notice, string? link, CancellationToken cancellationToken)
    {
        if (!Notices.IsWebAddress(url, out var address) || address.AbsolutePath.Trim('/') is not { Length: > 0 } topic || topic.Contains('/'))
        {
            return false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(address, "/"))
        {
            Content = JsonContent.Create(new { topic, title = notice.Title, message = notice.Body, click = link, tags = new[] { "books" } }),
        };
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await SendAsync(request, cancellationToken) is { IsSuccessStatusCode: true };
    }

    public async Task<bool> GotifyAsync(string url, string token, Notice notice, string? link, CancellationToken cancellationToken)
    {
        if (!Notices.IsWebAddress(url, out var address))
        {
            return false;
        }

        object extras = link is null ? new { } : new Dictionary<string, object> { ["client::notification"] = new { click = new { url = link } } };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(address.AbsoluteUri.TrimEnd('/') + "/message"))
        {
            Content = JsonContent.Create(new { title = notice.Title, message = notice.Body, priority = 5, extras }),
        };
        request.Headers.Add("X-Gotify-Key", token);
        return await SendAsync(request, cancellationToken) is { IsSuccessStatusCode: true };
    }

    public async Task<PushOutcome> PushAsync(PushSubscriptionRow subscription, Notice notice, CancellationToken cancellationToken)
    {
        if (!Notices.IsPushService(subscription.Endpoint, out var endpoint))
        {
            return PushOutcome.Gone;
        }

        byte[] body;
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { title = notice.Title, body = notice.Body, url = notice.Url, tag = notice.Tag });
            body = WebPush.Seal(payload, WebEncoders.Base64UrlDecode(subscription.P256dh), WebEncoders.Base64UrlDecode(subscription.Auth));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return PushOutcome.Gone;
        }

        var subject = configuration["Notices:Contact"] is { Length: > 0 } contact ? contact
            : configuration["Email:From"] is { Length: > 0 } from && from.Contains('@') ? "mailto:" + (from.Contains('<') ? from[(from.IndexOf('<') + 1)..].TrimEnd('>') : from)
            : "https://github.com/doodersrage/shelf";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.Add("TTL", "86400");
        request.Headers.Add("Urgency", "normal");
        request.Headers.TryAddWithoutValidation("Authorization", $"vapid t={keys.Token(endpoint.GetLeftPart(UriPartial.Authority), subject, DateTimeOffset.UtcNow.AddHours(12))}, k={keys.PublicKey}");
        return await SendAsync(request, cancellationToken) switch
        {
            { IsSuccessStatusCode: true } => PushOutcome.Sent,
            { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone } => PushOutcome.Gone,
            _ => PushOutcome.Failed,
        };
    }

    private async Task<HttpResponseMessage?> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }
}

// Message encryption for Web Push (RFC 8291, aes128gcm): a fresh key pair and salt for each message, agreed with the
// browser's key and mixed with its secret, so the push service carries it without reading it.
public static class WebPush
{
    private const int RecordSize = 4096;

    public static byte[] Seal(byte[] plaintext, byte[] browserKey, byte[] authSecret, byte[]? salt = null, ECDiffieHellman? ours = null)
    {
        if (browserKey.Length != 65 || browserKey[0] != 0x04 || authSecret.Length < 16 || plaintext.Length > RecordSize - 100)
        {
            throw new ArgumentException("A browser key of 65 bytes, a secret of 16, and a short message are needed.");
        }

        salt ??= RandomNumberGenerator.GetBytes(16);
        using var own = ours is null ? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256) : null;
        var server = ours ?? own!;
        using var browser = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = browserKey[1..33], Y = browserKey[33..65] },
        });
        var q = server.ExportParameters(false).Q;
        byte[] serverKey = [0x04, .. q.X!, .. q.Y!];
        var shared = server.DeriveRawSecretAgreement(browser.PublicKey);

        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, authSecret, [.. "WebPush: info\0"u8, .. browserKey, .. serverKey]);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, "Content-Encoding: nonce\0"u8.ToArray());

        // One record: the message, then 2 to say it is the last.
        byte[] padded = [.. plaintext, 0x02];
        var sealedText = new byte[padded.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
        {
            aes.Encrypt(nonce, padded, sealedText, tag);
        }

        var header = new byte[16 + 4 + 1 + serverKey.Length];
        salt.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), RecordSize);
        header[20] = (byte)serverKey.Length;
        serverKey.CopyTo(header, 21);
        return [.. header, .. sealedText, .. tag];
    }
}

// Tells readers what concerns them, by the ways each chose: this browser's notifications, an ntfy topic, Gotify, or,
// when the shelf can send email, their email address, which is on until they turn it off.
public sealed class Notices(IServiceScopeFactory scopes, INoticeChannels channels, IDataProtectionProvider protection, IConfiguration configuration, IEmailSender email, ILogger<Notices> logger)
    : BackgroundService, IShelfEventHandler
{
    public const int MaxDevices = 20;
    private IDataProtector Protector => protection.CreateProtector("Shelf.Notices.Tokens");

    public string PushKey => channels.PushKey;

    public static bool IsWebAddress(string? url, out Uri address)
    {
        address = null!;
        return Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var parsed) && parsed is { Scheme: "https" or "http", UserInfo.Length: 0 } && (address = parsed) is not null;
    }

    // Browsers' push services, and no other address: Google's for Chrome, Edge, and most others, Mozilla's, Apple's,
    // Microsoft's, and any an admin adds in Notices:PushHosts.
    public bool AllowsPush(string? endpoint) => IsPushService(endpoint, out _, configuration["Notices:PushHosts"]);

    public static bool IsPushService(string? endpoint, out Uri address, string? moreHosts = null)
    {
        address = null!;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps || endpoint!.Length > 1000)
        {
            return false;
        }

        string[] hosts = ["fcm.googleapis.com", "android.googleapis.com", "push.services.mozilla.com", "push.apple.com", "notify.windows.com", .. (moreHosts ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        address = parsed;
        return hosts.Any(host => parsed.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || parsed.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<NoticeSettings> SettingsAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var reader = await db.Readers.AsNoTracking().FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        var devices = (await db.PushSubscriptions.AsNoTracking().Where(item => item.ReaderId == db.ReaderId).ToListAsync(cancellationToken))
            .OrderByDescending(item => item.CreatedAt).Select(item => new PushDevice(item.Id, item.Label, item.CreatedAt)).ToArray();
        return new NoticeSettings((NoticeKinds)reader.NoticeKinds, reader.NtfyUrl, reader.NtfyToken is not null, reader.GotifyUrl, reader.GotifyToken is not null, devices,
            reader.DueSoonDays is >= 1 and <= 30 ? reader.DueSoonDays : Reader.DefaultDueSoonDays, reader.Email, reader.EmailNotices);
    }

    public static async Task SetEmailNoticesAsync(ShelfDb db, bool on, CancellationToken cancellationToken = default) =>
        await db.Readers.Where(item => item.Id == db.ReaderId).ExecuteUpdateAsync(set => set.SetProperty(item => item.EmailNotices, on), cancellationToken);

    public static async Task SetDueSoonDaysAsync(ShelfDb db, int days, CancellationToken cancellationToken = default) =>
        await db.Readers.Where(item => item.Id == db.ReaderId).ExecuteUpdateAsync(set => set.SetProperty(item => item.DueSoonDays, Math.Clamp(days, 1, 30)), cancellationToken);

    public static async Task SetKindsAsync(ShelfDb db, NoticeKinds kinds, CancellationToken cancellationToken = default)
    {
        var reader = await db.Readers.FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        reader.NoticeKinds = (int)(kinds & NoticeKinds.All);
        await db.SaveChangesAsync(cancellationToken);
    }

    // An ntfy topic's address (https://ntfy.sh/my-topic) and its token when the topic is protected; empty to stop.
    // A token left empty keeps the one already given for the same server.
    public async Task<bool> SetNtfyAsync(ShelfDb db, string? url, string? token, CancellationToken cancellationToken = default)
    {
        var reader = await db.Readers.FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        if (string.IsNullOrWhiteSpace(url))
        {
            (reader.NtfyUrl, reader.NtfyToken) = (null, null);
        }
        else if (IsWebAddress(url, out var address) && address.AbsoluteUri.Length <= 500 && address.AbsolutePath.Trim('/') is { Length: > 0 } topic && !topic.Contains('/'))
        {
            reader.NtfyToken = !string.IsNullOrWhiteSpace(token) ? Protector.Protect(token.Trim())
                : reader.NtfyUrl is not null && IsWebAddress(reader.NtfyUrl, out var before) && before.Authority == address.Authority ? reader.NtfyToken : null;
            reader.NtfyUrl = address.AbsoluteUri;
        }
        else
        {
            return false;
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // A Gotify server's address and an application token made on it; empty to stop.
    public async Task<bool> SetGotifyAsync(ShelfDb db, string? url, string? token, CancellationToken cancellationToken = default)
    {
        var reader = await db.Readers.FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        if (string.IsNullOrWhiteSpace(url))
        {
            (reader.GotifyUrl, reader.GotifyToken) = (null, null);
        }
        else if (IsWebAddress(url, out var address) && address.AbsoluteUri.Length <= 500 && (!string.IsNullOrWhiteSpace(token) || reader.GotifyToken is not null))
        {
            reader.GotifyToken = string.IsNullOrWhiteSpace(token) ? reader.GotifyToken : Protector.Protect(token.Trim());
            reader.GotifyUrl = address.AbsoluteUri;
        }
        else
        {
            return false;
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> AddDeviceAsync(ShelfDb db, string? endpoint, string? p256dh, string? auth, string? label, CancellationToken cancellationToken = default)
    {
        if (!AllowsPush(endpoint) || p256dh is not { Length: > 80 and < 200 } || auth is not { Length: > 15 and < 100 })
        {
            return false;
        }

        try
        {
            if (WebEncoders.Base64UrlDecode(p256dh).Length != 65 || WebEncoders.Base64UrlDecode(auth).Length < 16)
            {
                return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }

        // A browser subscribes once; subscribing again replaces it, whoever it was for before.
        await db.PushSubscriptions.Where(item => item.Endpoint == endpoint).ExecuteDeleteAsync(cancellationToken);
        var count = await db.PushSubscriptions.CountAsync(item => item.ReaderId == db.ReaderId, cancellationToken);
        if (count >= MaxDevices)
        {
            var oldest = (await db.PushSubscriptions.Where(item => item.ReaderId == db.ReaderId).ToListAsync(cancellationToken)).OrderBy(item => item.CreatedAt).First();
            db.PushSubscriptions.Remove(oldest);
        }

        db.PushSubscriptions.Add(new PushSubscriptionRow
        {
            ReaderId = db.ReaderId,
            Endpoint = endpoint!,
            P256dh = p256dh,
            Auth = auth,
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim()[..Math.Min(label.Trim().Length, 120)],
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public static async Task RemoveDeviceAsync(ShelfDb db, int? id, string? endpoint = null, CancellationToken cancellationToken = default) =>
        await db.PushSubscriptions.Where(item => item.ReaderId == db.ReaderId && (item.Id == id || item.Endpoint == endpoint)).ExecuteDeleteAsync(cancellationToken);

    // A notification by every way the reader set up, whatever kinds they chose. Returns how many ways took it.
    public Task<int> TestAsync(int readerId, CancellationToken cancellationToken = default) =>
        SendAsync(readerId, null, _ => new Notice(T("Shelf"), T("Notifications reach you here."), "/account#notices", "test"), cancellationToken);

    // byEmail false leaves email out, for news that goes by an email of its own (new books in a series).
    public async Task<int> SendAsync(int readerId, NoticeKinds? kind, Func<Reader, Notice?> compose, CancellationToken cancellationToken = default, bool byEmail = true)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var reader = await db.Readers.AsNoTracking().FirstOrDefaultAsync(item => item.Id == readerId, cancellationToken);
        if (reader is null || (kind is { } wanted && ((NoticeKinds)reader.NoticeKinds & wanted) == 0))
        {
            return 0;
        }

        var devices = await db.PushSubscriptions.Where(item => item.ReaderId == readerId).ToListAsync(cancellationToken);
        var address = byEmail && email.Enabled && reader.EmailNotices ? reader.Email : null;
        if (devices.Count == 0 && reader.NtfyUrl is null && reader.GotifyUrl is null && address is null)
        {
            return 0;
        }

        EmailMessage? mail = null;

        Notice? notice;
        var site = EmailRules.PublicAddress(configuration, null);
        using (Speaking(reader.Language))
        {
            notice = compose(reader);
            if (notice is not null && address is not null)
            {
                mail = new EmailMessage(address, notice.Title, T("Hello {0},", reader.Name) + "\n\n" + notice.Body + "\n\n"
                    + (site.Length > 0 ? T("See it on Shelf: {0}", site + notice.Url) + "\n\n" : "")
                    + T("You can choose what you hear about, and how, on your account under Notifications."));
            }
        }

        if (notice is null)
        {
            return 0;
        }

        notice = notice with { Title = Clip(notice.Title, 120), Body = Clip(notice.Body, 600) };
        var link = site.Length > 0 ? site + notice.Url : null;
        var sent = 0;
        if (mail is not null)
        {
            try
            {
                await email.SendAsync(mail, cancellationToken);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "A notification email to reader {ReaderId} did not go.", readerId);
            }
        }
        if (reader.NtfyUrl is { } ntfy && await channels.NtfyAsync(ntfy, Open(reader.NtfyToken), notice, link, cancellationToken))
        {
            sent++;
        }

        if (reader.GotifyUrl is { } gotify && Open(reader.GotifyToken) is { } gotifyToken && await channels.GotifyAsync(gotify, gotifyToken, notice, link, cancellationToken))
        {
            sent++;
        }

        foreach (var device in devices)
        {
            switch (await channels.PushAsync(device, notice, cancellationToken))
            {
                case PushOutcome.Sent:
                    sent++;
                    break;
                case PushOutcome.Gone:
                    db.PushSubscriptions.Remove(device);
                    break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return sent;
    }

    public async Task HandleAsync(ShelfEvent shelfEvent, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var book = shelfEvent.BookId is int bookId
            ? await db.Books.IgnoreQueryFilters().AsNoTracking().Where(item => item.Id == bookId).Select(item => new { item.Id, item.Title, item.OwnerId }).FirstOrDefaultAsync(cancellationToken)
            : null;
        async Task<string> NameAsync(int? id) => id is null ? "" : await db.Readers.AsNoTracking().Where(item => item.Id == id).Select(item => item.Name).FirstOrDefaultAsync(cancellationToken) ?? "";

        switch (shelfEvent.Kind)
        {
            case EventKinds.BookLent when book is not null && shelfEvent.ReaderId is int borrower && borrower != shelfEvent.ActorId:
                var lender = await NameAsync(book.OwnerId);
                await SendAsync(borrower, NoticeKinds.Loans, _ => new Notice(T("{0} lent you a book", lender), book.Title, "/loans", $"loan-{book.Id}"), cancellationToken);
                break;
            case EventKinds.BookReturned when book?.OwnerId is int owner:
                if (shelfEvent.ActorId != owner && shelfEvent.ReaderId is int returner)
                {
                    var returnerName = await NameAsync(returner);
                    await SendAsync(owner, NoticeKinds.Loans, _ => new Notice(T("{0} returned a book", returnerName), book.Title, $"/library/{book.Id}", $"loan-{book.Id}"), cancellationToken);
                }

                // Whoever asked first, other than the one who had it, hears it is free again.
                var next = await db.LoanAsks.AsNoTracking().Where(ask => ask.BookId == book.Id && ask.ReaderId != shelfEvent.ReaderId)
                    .OrderBy(ask => ask.Id).Select(ask => (int?)ask.ReaderId).FirstOrDefaultAsync(cancellationToken);
                if (next is int waiting)
                {
                    var ownerName = await NameAsync(owner);
                    await SendAsync(waiting, NoticeKinds.Asks, _ => new Notice(T("A book you asked for is back"), T("{0}, from {1}. You are first in line.", book.Title, ownerName), "/loans", $"ask-{book.Id}"), cancellationToken);
                }

                break;
            case EventKinds.LoanAsked when book?.OwnerId is int asked && shelfEvent.ReaderId is int asker:
                var askerName = await NameAsync(asker);
                await SendAsync(asked, NoticeKinds.Asks, _ => new Notice(T("{0} asks to borrow a book", askerName), book.Title, "/loans", $"ask-{book.Id}"), cancellationToken);
                break;
            case EventKinds.SeriesFound when shelfEvent.ReaderId is int seriesReader:
                await SendAsync(seriesReader, NoticeKinds.Series, _ => new Notice(
                    shelfEvent.Count == 1 ? T("A new book in your series") : T("{0} new books in your series", shelfEvent.Count),
                    T("See them under Series."), "/series", "series"), cancellationToken, byEmail: false);
                break;
            case EventKinds.ClubJoined when shelfEvent.ClubId is int clubId && shelfEvent.ReaderId is int member && member != shelfEvent.ActorId:
                var club = await db.Clubs.AsNoTracking().Where(item => item.Id == clubId).Select(item => item.Name).FirstOrDefaultAsync(cancellationToken);
                var inviter = await NameAsync(shelfEvent.ActorId);
                if (club is not null)
                {
                    await SendAsync(member, NoticeKinds.Clubs, _ => new Notice(T("{0} added you to a book club", inviter), club, $"/clubs/{clubId}", $"club-{clubId}"), cancellationToken);
                }

                break;
            case EventKinds.ClubMeeting when shelfEvent.MeetingId is int meetingId:
                var meeting = await db.ClubMeetings.AsNoTracking().Where(item => item.Id == meetingId)
                    .Select(item => new { item.ClubId, Club = item.Club!.Name, BookTitle = item.Book!.Title, item.At, item.TimeZone, item.Through }).FirstOrDefaultAsync(cancellationToken);
                if (meeting is null)
                {
                    break;
                }

                var invited = await db.ClubMembers.AsNoTracking().Where(item => item.ClubId == meeting.ClubId && item.ReaderId != shelfEvent.ActorId).Select(item => item.ReaderId).ToListAsync(cancellationToken);
                foreach (var reader in invited)
                {
                    await SendAsync(reader, NoticeKinds.Clubs, _ => new Notice(
                        T("{0} meets {1}", meeting.Club, ClubMeetings.Say(meeting.At, meeting.TimeZone)),
                        meeting.Through is null ? meeting.BookTitle : T("{0}, {1}", meeting.BookTitle, meeting.Through),
                        $"/clubs/{meeting.ClubId}", $"club-{meeting.ClubId}"), cancellationToken);
                }

                break;
            case EventKinds.ClubPosted when shelfEvent.PostId is int postId:
                var post = await db.ClubPosts.AsNoTracking().Where(item => item.Id == postId)
                    .Select(item => new { item.ReaderId, Text = item.Spoiler ? null : item.Text, item.Book!.ClubId, Club = item.Book.Club!.Name, BookTitle = item.Book.Title }).FirstOrDefaultAsync(cancellationToken);
                if (post is null)
                {
                    break;
                }

                var poster = await NameAsync(post.ReaderId);
                var members = await db.ClubMembers.AsNoTracking().Where(item => item.ClubId == post.ClubId && item.ReaderId != post.ReaderId).Select(item => item.ReaderId).ToListAsync(cancellationToken);
                foreach (var reader in members)
                {
                    await SendAsync(reader, NoticeKinds.Clubs, _ => new Notice(T("{0} in {1}", poster, post.Club), $"{post.BookTitle}: {post.Text ?? T("(a spoiler, in the club)")}", $"/clubs/{post.ClubId}", $"club-{post.ClubId}"), cancellationToken);
                }

                break;
            case EventKinds.ClubReading when shelfEvent.ClubId is int readingIn && shelfEvent.ClubBookId is int picked:
                var reading = await db.ClubBooks.AsNoTracking().Where(item => item.Id == picked).Select(item => new { Club = item.Club!.Name, item.Title, item.Author }).FirstOrDefaultAsync(cancellationToken);
                if (reading is null)
                {
                    break;
                }

                foreach (var reader in await MembersAsync(db, readingIn, shelfEvent.ActorId, cancellationToken))
                {
                    await SendAsync(reader, NoticeKinds.Clubs, _ => new Notice(T("{0} is reading a new book", reading.Club), string.IsNullOrWhiteSpace(reading.Author) ? reading.Title : T("{0}, by {1}", reading.Title, reading.Author), $"/clubs/{readingIn}", $"club-{readingIn}"), cancellationToken);
                }

                break;
            case EventKinds.ClubMeetingCancelled when shelfEvent.ClubId is int calledOffIn && shelfEvent.When is { } calledOffAt:
                var calledOff = await db.Clubs.AsNoTracking().Where(item => item.Id == calledOffIn).Select(item => item.Name).FirstOrDefaultAsync(cancellationToken);
                var calledOffBook = shelfEvent.ClubBookId is int calledOffBookId ? await db.ClubBooks.AsNoTracking().Where(item => item.Id == calledOffBookId).Select(item => item.Title).FirstOrDefaultAsync(cancellationToken) : null;
                if (calledOff is null)
                {
                    break;
                }

                foreach (var reader in await MembersAsync(db, calledOffIn, shelfEvent.ActorId, cancellationToken))
                {
                    await SendAsync(reader, NoticeKinds.Clubs, _ => new Notice(T("{0} will not meet {1}", calledOff, ClubMeetings.Say(calledOffAt, shelfEvent.Zone)), calledOffBook ?? T("The meeting is called off."), $"/clubs/{calledOffIn}", $"club-{calledOffIn}"), cancellationToken);
                }

                break;
            case EventKinds.ClubLeft when shelfEvent.ClubId is int leftClub && shelfEvent.ReaderId is int left:
                var leftName = await db.Clubs.AsNoTracking().Where(item => item.Id == leftClub).Select(item => item.Name).FirstOrDefaultAsync(cancellationToken);
                var remover = await NameAsync(shelfEvent.ActorId);
                if (leftName is not null)
                {
                    await SendAsync(left, NoticeKinds.Clubs, _ => new Notice(T("You are no longer in {0}", leftName), T("{0} took you out of the book club.", remover), "/clubs", $"club-{leftClub}"), cancellationToken);
                }

                break;
            case EventKinds.ClubEnded when shelfEvent.ReaderId is int member && shelfEvent.Name is { } endedName:
                var ender = await NameAsync(shelfEvent.ActorId);
                await SendAsync(member, NoticeKinds.Clubs, _ => new Notice(T("{0} has ended", endedName), T("{0} ended the book club.", ender), "/clubs", "club-ended"), cancellationToken);
                break;
            case EventKinds.AskDeclined when book?.OwnerId is int declinedBy && shelfEvent.ReaderId is int declined:
                var declinerName = await NameAsync(declinedBy);
                await SendAsync(declined, NoticeKinds.Asks, _ => new Notice(T("{0} can't lend you a book", declinerName), book.Title, "/loans", $"ask-{book.Id}"), cancellationToken);
                break;
            case EventKinds.ReaderJoined when shelfEvent.ReaderId is int joined:
                var newcomer = await db.Readers.AsNoTracking().Where(item => item.Id == joined).Select(item => new { item.Name, item.Restricted, item.GuardianId }).FirstOrDefaultAsync(cancellationToken);
                if (newcomer is null)
                {
                    break;
                }

                var guardian = newcomer.Restricted ? await NameAsync(newcomer.GuardianId) : null;
                foreach (var admin in await db.Readers.AsNoTracking().Where(item => item.IsAdmin && item.Id != joined).Select(item => item.Id).ToListAsync(cancellationToken))
                {
                    await SendAsync(admin, NoticeKinds.Admin, _ => new Notice(T("{0} joined the shelf", newcomer.Name),
                        guardian is null ? T("A new reader signed up.") : T("{0} made a child's account.", guardian), "/admin", "reader-joined"), cancellationToken);
                }

                break;
            case EventKinds.EmailChanged or EventKinds.PasswordChanged or EventKinds.TwoStepOff or EventKinds.PasskeyAdded or EventKinds.SignedIn when shelfEvent.ReaderId is int account:
                await SecureAsync(db, account, shelfEvent, cancellationToken);
                break;
        }
    }

    // A club's members, other than whoever did the thing they are told of.
    private static Task<List<int>> MembersAsync(ShelfDb db, int clubId, int except, CancellationToken cancellationToken) =>
        db.ClubMembers.AsNoTracking().Where(item => item.ClubId == clubId && item.ReaderId != except).Select(item => item.ReaderId).ToListAsync(cancellationToken);

    // Changes to how a reader signs in come by email whatever they chose to hear about, as someone else may have made
    // them: to the address they had, when it is the address that changed. A sign-in says so only from a device the
    // shelf has not seen them on.
    private async Task SecureAsync(ShelfDb db, int readerId, ShelfEvent shelfEvent, CancellationToken cancellationToken)
    {
        var reader = await db.Readers.FirstOrDefaultAsync(item => item.Id == readerId, cancellationToken);
        if (reader is null || reader.SandboxOf is not null)
        {
            return;
        }

        if (shelfEvent.Kind == EventKinds.SignedIn)
        {
            // A device already known, or the first sign-in the shelf keeps track of (signing up, or the first after
            // this came in), says nothing.
            var device = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(shelfEvent.Name ?? "")))[..16];
            var known = (reader.KnownDevices ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
            var seen = reader.KnownDevices is null || known.Remove(device);
            reader.KnownDevices = string.Join('\n', known.Prepend(device).Take(30));
            await db.SaveChangesAsync(cancellationToken);
            if (seen)
            {
                return;
            }
        }

        var to = shelfEvent.Kind == EventKinds.EmailChanged ? shelfEvent.Name : reader.Email;
        if (!email.Enabled || to is null)
        {
            return;
        }

        var site = EmailRules.PublicAddress(configuration, null);
        EmailMessage message;
        using (Speaking(reader.Language))
        {
            var account = site.Length > 0 ? "\n\n" + T("Open your account: {0}", site + "/account#sessions") : "";
            var reset = site.Length > 0 ? "\n\n" + T("Reset your password: {0}", site + "/forgot") : "";
            message = shelfEvent.Kind switch
            {
                EventKinds.EmailChanged => new EmailMessage(to, T("Your Shelf email address changed"),
                    T("Hello {0},\n\nThe email address you sign in to Shelf with is now {1}, in place of this one.", reader.Name, reader.Email ?? "") + reset
                    + "\n\n" + T("If you did not change it, someone else may be in your account: reset your password, or ask an admin of the shelf for help.")),
                EventKinds.PasswordChanged => new EmailMessage(to, T("Your Shelf password changed"),
                    T("Hello {0},\n\nThe password for your shelf was just changed, and every device signed in with the old one was signed out.", reader.Name) + reset
                    + "\n\n" + T("If it was not you, reset your password now, or ask an admin of the shelf for help.")),
                EventKinds.TwoStepOff => new EmailMessage(to, T("Two-step sign-in is off"),
                    T("Hello {0},\n\nTwo-step sign-in was just turned off for your shelf, so a password alone signs in now.", reader.Name) + account
                    + "\n\n" + T("If it was not you, change your password, and turn two-step sign-in on again.")),
                EventKinds.PasskeyAdded => new EmailMessage(to, T("A passkey was added to your account"),
                    T("Hello {0},\n\nA new passkey, {1}, can now sign in to your shelf.", reader.Name, shelfEvent.Name ?? "") + account
                    + "\n\n" + T("If it was not you, remove it under Sign-in & security, and change your password.")),
                _ => new EmailMessage(to, T("A new sign-in to your shelf"),
                    T("Hello {0},\n\nYour shelf was just signed in to from {1}, a device you had not used with it before.", reader.Name, string.IsNullOrWhiteSpace(shelfEvent.Name) ? T("A browser") : shelfEvent.Name) + account
                    + "\n\n" + T("If it was you, there is nothing to do. If not, sign that device out under Sign-in & security, and change your password.")),
            };
        }

        try
        {
            await email.SendAsync(message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "A security email to reader {ReaderId} did not go.", readerId);
        }
    }

    // Every five minutes, members hear of their club's meetings a day and an hour ahead; every hour, readers who are
    // told of their loans hear once a day of any due soon or overdue, by email too, which takes the place of the
    // daily loan email there was before notifications; and admins hear of anything wrong on Server health.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        var hourly = DateTimeOffset.MinValue;
        do
        {
            try
            {
                await RemindMeetingsAsync(stoppingToken);
                if (DateTimeOffset.UtcNow - hourly >= TimeSpan.FromHours(1))
                {
                    await RemindDueAsync(stoppingToken);
                    await TellHealthAsync(stoppingToken);
                    hourly = DateTimeOffset.UtcNow;
                }

                Heartbeats.Beat(nameof(Notices));
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                Heartbeats.Beat(nameof(Notices), ex.Message);
                logger.LogWarning(ex, "Could not send notifications; they will try again shortly.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // A meeting a day away, and an hour away, is told to every member of its club, each once. A meeting arranged
    // closer than that has just been told of, and a meeting already past is let be.
    public async Task<int> RemindMeetingsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var waiting = await db.ClubMeetings.Where(meeting => !meeting.HourReminded).Include(meeting => meeting.Club).Include(meeting => meeting.Book).ToListAsync(cancellationToken);
        var sent = 0;
        foreach (var meeting in waiting)
        {
            var away = meeting.At - now;
            var dayAhead = !meeting.DayReminded && away <= TimeSpan.FromDays(1);
            var hourAhead = away <= TimeSpan.FromHours(1);
            if (!dayAhead && !hourAhead)
            {
                continue;
            }

            var lead = hourAhead ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
            (meeting.DayReminded, meeting.HourReminded) = (true, hourAhead || meeting.HourReminded);
            await db.SaveChangesAsync(cancellationToken);
            if (away <= TimeSpan.Zero || meeting.At - meeting.CreatedAt <= lead || meeting.Club is null)
            {
                continue;
            }

            var (club, title, through, at, zone, clubId) = (meeting.Club.Name, meeting.Book?.Title ?? "", meeting.Through, meeting.At, meeting.TimeZone, meeting.ClubId);
            foreach (var reader in await MembersAsync(db, clubId, 0, cancellationToken))
            {
                sent += await SendAsync(reader, NoticeKinds.Clubs, _ => new Notice(
                    T("Reminder: {0} meets {1}", club, ClubMeetings.Say(at, zone)),
                    through is null ? title : T("{0}, {1}", title, through),
                    $"/clubs/{clubId}", $"club-{clubId}"), cancellationToken) > 0 ? 1 : 0;
            }
        }

        return sent;
    }

    // Admins hear when something on Server health goes wrong: once for each new set of troubles, not every hour.
    public async Task<int> TellHealthAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var report = await scope.ServiceProvider.GetRequiredService<ShelfHealth>().ReportAsync(db, cancellationToken);
        var wrong = report.Items.Where(item => item.State == HealthState.Wrong).ToList();
        var troubles = wrong.Count == 0 ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', wrong.Select(item => $"{item.Area}|{item.Name}").Order(StringComparer.Ordinal)))))[..32];
        var sent = 0;
        foreach (var admin in await db.Readers.Where(reader => reader.IsAdmin && reader.HealthTold != troubles).ToListAsync(cancellationToken))
        {
            admin.HealthTold = troubles;
            await db.SaveChangesAsync(cancellationToken);
            if (troubles is not null)
            {
                sent += await SendAsync(admin.Id, NoticeKinds.Admin, _ => new Notice(
                    wrong.Count == 1 ? T("Something on the shelf needs you") : T("{0} things on the shelf need you", wrong.Count),
                    string.Join(" ", wrong.Take(3).Select(item => item.Says)), "/admin/health", "health"), cancellationToken) > 0 ? 1 : 0;
            }
        }

        return sent;
    }

    public async Task<int> RemindDueAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        List<int> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var withDevice = db.PushSubscriptions.Select(item => item.ReaderId);
            var mails = email.Enabled;
            due = await db.Readers.AsNoTracking()
                .Where(reader => (reader.NoticeKinds & (int)NoticeKinds.Loans) != 0 && (reader.LastNoticeOn == null || reader.LastNoticeOn < today)
                    && (reader.NtfyUrl != null || reader.GotifyUrl != null || withDevice.Contains(reader.Id) || (mails && reader.EmailNotices && reader.Email != null)))
                .Select(reader => reader.Id)
                .ToListAsync(cancellationToken);
        }

        var sent = 0;
        foreach (var readerId in due)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var reminders = await Asking.RemindersAsync(db, cancellationToken);
            if (reminders.LentOverdue + reminders.BorrowedOverdue + reminders.BorrowedDueSoon > 0)
            {
                sent += await SendAsync(readerId, NoticeKinds.Loans, _ => new Notice(T("Your loans on Shelf"), string.Join(" ", new[]
                {
                    reminders.LentOverdue == 0 ? null : reminders.LentOverdue == 1 ? T("1 book you lent is overdue.") : T("{0} books you lent are overdue.", reminders.LentOverdue),
                    reminders.BorrowedOverdue == 0 ? null : reminders.BorrowedOverdue == 1 ? T("1 book you borrowed is overdue.") : T("{0} books you borrowed are overdue.", reminders.BorrowedOverdue),
                    reminders.BorrowedDueSoon == 0 ? null : reminders.BorrowedDueSoon == 1 ? T("1 borrowed book is due within {0} days.", reminders.SoonDays) : T("{0} borrowed books are due within {1} days.", reminders.BorrowedDueSoon, reminders.SoonDays),
                }.OfType<string>()), "/loans", "loans-due"), cancellationToken) > 0 ? 1 : 0;
            }

            await db.Readers.Where(item => item.Id == readerId).ExecuteUpdateAsync(set => set.SetProperty(item => item.LastNoticeOn, today), cancellationToken);
        }

        return sent;
    }

    private string? Open(string? sealedToken)
    {
        try
        {
            return sealedToken is null ? null : Protector.Unprotect(sealedToken);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static string Clip(string text, int most) => text.Length <= most ? text : text[..(most - 1)] + "…";
}

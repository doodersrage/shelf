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
// new books in their series, and their book clubs.
[Flags]
public enum NoticeKinds
{
    None = 0,
    Loans = 1,
    Asks = 2,
    Series = 4,
    Clubs = 8,
    All = Loans | Asks | Series | Clubs,
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

public sealed record NoticeSettings(NoticeKinds Kinds, string? NtfyUrl, bool NtfyToken, string? GotifyUrl, bool GotifyToken, PushDevice[] Devices);

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

// Tells readers what concerns them, by the ways each chose: this browser's notifications, an ntfy topic, or Gotify.
public sealed class Notices(IServiceScopeFactory scopes, INoticeChannels channels, IDataProtectionProvider protection, IConfiguration configuration, ILogger<Notices> logger)
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
        return new NoticeSettings((NoticeKinds)reader.NoticeKinds, reader.NtfyUrl, reader.NtfyToken is not null, reader.GotifyUrl, reader.GotifyToken is not null, devices);
    }

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

    public async Task<int> SendAsync(int readerId, NoticeKinds? kind, Func<Reader, Notice?> compose, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var reader = await db.Readers.AsNoTracking().FirstOrDefaultAsync(item => item.Id == readerId, cancellationToken);
        if (reader is null || (kind is { } wanted && ((NoticeKinds)reader.NoticeKinds & wanted) == 0))
        {
            return 0;
        }

        var devices = await db.PushSubscriptions.Where(item => item.ReaderId == readerId).ToListAsync(cancellationToken);
        if (devices.Count == 0 && reader.NtfyUrl is null && reader.GotifyUrl is null)
        {
            return 0;
        }

        Notice? notice;
        using (Speaking(reader.Language))
        {
            notice = compose(reader);
        }

        if (notice is null)
        {
            return 0;
        }

        notice = notice with { Title = Clip(notice.Title, 120), Body = Clip(notice.Body, 600) };
        var site = EmailRules.PublicAddress(configuration, null);
        var link = site.Length > 0 ? site + notice.Url : null;
        var sent = 0;
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
            case EventKinds.BookReturned when book?.OwnerId is int owner && shelfEvent.ActorId != owner && shelfEvent.ReaderId is int returner:
                var returnerName = await NameAsync(returner);
                await SendAsync(owner, NoticeKinds.Loans, _ => new Notice(T("{0} returned a book", returnerName), book.Title, $"/library/{book.Id}", $"loan-{book.Id}"), cancellationToken);
                break;
            case EventKinds.LoanAsked when book?.OwnerId is int asked && shelfEvent.ReaderId is int asker:
                var askerName = await NameAsync(asker);
                await SendAsync(asked, NoticeKinds.Asks, _ => new Notice(T("{0} asks to borrow a book", askerName), book.Title, "/loans", $"ask-{book.Id}"), cancellationToken);
                break;
            case EventKinds.SeriesFound when shelfEvent.ReaderId is int seriesReader:
                await SendAsync(seriesReader, NoticeKinds.Series, _ => new Notice(
                    shelfEvent.Count == 1 ? T("A new book in your series") : T("{0} new books in your series", shelfEvent.Count),
                    T("See them under Series."), "/series", "series"), cancellationToken);
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
                    .Select(item => new { item.ClubId, Club = item.Club!.Name, BookTitle = item.Book!.Title, item.At, item.Through }).FirstOrDefaultAsync(cancellationToken);
                if (meeting is null)
                {
                    break;
                }

                var invited = await db.ClubMembers.AsNoTracking().Where(item => item.ClubId == meeting.ClubId && item.ReaderId != shelfEvent.ActorId).Select(item => item.ReaderId).ToListAsync(cancellationToken);
                foreach (var reader in invited)
                {
                    await SendAsync(reader, NoticeKinds.Clubs, _ => new Notice(
                        T("{0} meets {1}", meeting.Club, meeting.At.Medium()),
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
        }
    }

    // Once a day, readers who are told of their loans hear of any due soon or overdue.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await RemindDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Could not send loan notifications; they will try again in an hour.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> RemindDueAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        List<int> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var withDevice = db.PushSubscriptions.Select(item => item.ReaderId);
            due = await db.Readers.AsNoTracking()
                .Where(reader => (reader.NoticeKinds & (int)NoticeKinds.Loans) != 0 && (reader.LastNoticeOn == null || reader.LastNoticeOn < today)
                    && (reader.NtfyUrl != null || reader.GotifyUrl != null || withDevice.Contains(reader.Id)))
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
                    reminders.BorrowedDueSoon == 0 ? null : reminders.BorrowedDueSoon == 1 ? T("1 borrowed book is due within three days.") : T("{0} borrowed books are due within three days.", reminders.BorrowedDueSoon),
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

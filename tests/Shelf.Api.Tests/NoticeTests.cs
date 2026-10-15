using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class NoticeTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task Readers_hear_of_asks_loans_and_club_posts_by_the_ways_they_chose()
    {
        var owner = await factory.SignUpAsync("Notice Owner");
        var friend = await factory.SignUpAsync("Notice Friend");
        var ownerId = await factory.ReaderIdAsync("Notice Owner");
        var friendId = await factory.ReaderIdAsync("Notice Friend");
        var notices = factory.Services.GetRequiredService<Notices>();
        await using (var scope = Scope(ownerId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            Assert.False(await notices.SetNtfyAsync(db, "https://ntfy.sh/", null));
            Assert.True(await notices.SetNtfyAsync(db, "https://ntfy.sh/owner-topic", "tk_secret"));
        }

        await using (var scope = Scope(friendId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            Assert.False(await notices.SetGotifyAsync(db, "https://gotify.example.com", null));
            Assert.True(await notices.SetGotifyAsync(db, "https://gotify.example.com", "app-token"));
            await Notices.SetKindsAsync(db, NoticeKinds.All & ~NoticeKinds.Clubs);
        }

        // The friend asks to borrow; the owner hears of it by ntfy, with their token.
        var book = (await (await owner.PostAsJsonAsync("/books", new CreateBookRequest("Middlemarch", "George Eliot", BookStatus.Finished, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        await owner.PutAsJsonAsync("/books/shelves/open", new ShelfOpenChange(true), JsonOptions);
        (await friend.PostAsync($"/books/{book.Id}/ask", null)).EnsureSuccessStatusCode();
        var asked = await factory.Channels.WaitAsync(sent => sent.Address == "https://ntfy.sh/owner-topic");
        Assert.Equal(("https://ntfy.sh/owner-topic", "tk_secret", "Notice Friend asks to borrow a book", "Middlemarch", "/loans"), (asked.Address, asked.Token, asked.Notice.Title, asked.Notice.Body, asked.Notice.Url));

        // Lent: the friend hears by Gotify.
        await owner.PostAsJsonAsync($"/books/{book.Id}/lend", new LendRequest(friendId), JsonOptions);
        var lent = await factory.Channels.WaitAsync(sent => sent.Address == "https://gotify.example.com/");
        Assert.Equal(("app-token", "Notice Owner lent you a book"), (lent.Token, lent.Notice.Title));

        // A club post reaches the owner, but not the friend, who turned clubs off, nor the one who wrote it.
        var club = (await (await owner.PostAsJsonAsync("/books/clubs", new ClubRequest("Notice club", null), JsonOptions)).Content.ReadFromJsonAsync<ClubResponse>(JsonOptions))!;
        await owner.PostAsJsonAsync($"/books/clubs/{club.Id}/members", new ClubInvite(friendId), JsonOptions);
        await owner.PostAsJsonAsync($"/books/clubs/{club.Id}/books", new ClubBookRequest(BookId: book.Id), JsonOptions);
        var clubBook = (await friend.GetFromJsonAsync<ClubResponse>($"/books/clubs/{club.Id}", JsonOptions))!.Books.Single();
        await friend.PostAsJsonAsync($"/books/clubs/{club.Id}/books/{clubBook.Id}/posts", new ClubPostRequest("Dorothea!"), JsonOptions);
        var posted = await factory.Channels.WaitAsync(sent => sent.Notice.Body.Contains("Dorothea", StringComparison.Ordinal));
        Assert.Equal(("ntfy", "Notice Friend in Notice club", "Middlemarch: Dorothea!"), (posted.Way, posted.Notice.Title, posted.Notice.Body));
        await WaitIdleAsync();
        Assert.DoesNotContain(factory.Channels.Sent, sent => sent.Address == "https://gotify.example.com/" && sent.Notice.Url.StartsWith("/clubs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_device_the_push_service_has_forgotten_is_forgotten_here_too()
    {
        await factory.SignUpAsync("Push Reader");
        var readerId = await factory.ReaderIdAsync("Push Reader");
        var notices = factory.Services.GetRequiredService<Notices>();
        var (p256dh, auth) = BrowserKeys();
        await using (var scope = Scope(readerId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            Assert.False(await notices.AddDeviceAsync(db, "https://evil.example.com/push/1", p256dh, auth, "Chrome on Linux"));
            Assert.True(await notices.AddDeviceAsync(db, "https://fcm.googleapis.com/fcm/send/gone", p256dh, auth, "Chrome on Linux"));
            Assert.True(await notices.AddDeviceAsync(db, "https://updates.push.services.mozilla.com/wpush/v2/kept", p256dh, auth, "Firefox on Linux"));
            Assert.Equal(2, (await Notices.SettingsAsync(db)).Devices.Length);
        }

        // The browser still subscribed takes it, and so does the reader's email address; the forgotten one does not.
        Assert.Equal(2, await notices.TestAsync(readerId));
        await using (var scope = Scope(readerId))
        {
            var devices = (await Notices.SettingsAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>())).Devices;
            Assert.Equal("Firefox on Linux", Assert.Single(devices).Label);
        }
    }

    [Fact]
    public void A_push_message_is_sealed_so_only_the_browser_can_open_it()
    {
        using var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = browser.ExportParameters(false).Q;
        byte[] browserKey = [0x04, .. q.X!, .. q.Y!];
        var secret = RandomNumberGenerator.GetBytes(16);
        var message = Encoding.UTF8.GetBytes("""{"title":"Prêté","body":"Middlemarch"}""");

        var sealedMessage = WebPush.Seal(message, browserKey, secret);
        Assert.Equal(message, Open(sealedMessage, browser, browserKey, secret));
        Assert.Throws<AuthenticationTagMismatchException>(() => Open(sealedMessage, browser, browserKey, RandomNumberGenerator.GetBytes(16)));
    }

    [Fact]
    public void The_shelfs_push_key_signs_its_tokens_and_is_kept_for_next_time()
    {
        var folder = Directory.CreateTempSubdirectory("shelf-push-").FullName;
        try
        {
            var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(folder, "keys")));
            var keys = new WebPushKeys(folder, protection);
            Assert.Equal(keys.PublicKey, new WebPushKeys(folder, protection).PublicKey);

            var token = keys.Token("https://fcm.googleapis.com", "mailto:shelf@example.com", DateTimeOffset.UtcNow.AddHours(12)).Split('.');
            var point = WebEncoders.Base64UrlDecode(keys.PublicKey);
            using var check = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = point[1..33], Y = point[33..] } });
            Assert.True(check.VerifyData(Encoding.ASCII.GetBytes($"{token[0]}.{token[1]}"), WebEncoders.Base64UrlDecode(token[2]), HashAlgorithmName.SHA256));
            Assert.Contains("\"aud\":\"https://fcm.googleapis.com\"", Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token[1])), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task The_channels_send_ntfy_json_to_the_servers_root_and_gotify_its_message()
    {
        var asked = new ConcurrentQueue<(string Url, string Body, string Headers)>();
        var handler = new Answers(async request =>
        {
            asked.Enqueue((request.RequestUri!.AbsoluteUri, await request.Content!.ReadAsStringAsync(), request.Headers.ToString()));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var folder = Directory.CreateTempSubdirectory("shelf-push-").FullName;
        try
        {
            var channels = new NoticeChannels(new HttpClient(handler), new WebPushKeys(folder, DataProtectionProvider.Create("test")), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
            var notice = new Notice("Prêté", "Middlemarch", "/loans", "loan-1");
            Assert.True(await channels.NtfyAsync("https://ntfy.example.com/my-topic", "tk_1", notice, "https://shelf.example.com/loans", CancellationToken.None));
            Assert.True(await channels.GotifyAsync("https://gotify.example.com/", "app", notice, null, CancellationToken.None));
            var (p256dh, auth) = BrowserKeys();
            Assert.Equal(PushOutcome.Sent, await channels.PushAsync(new PushSubscriptionRow { Endpoint = "https://fcm.googleapis.com/fcm/send/abc", P256dh = p256dh, Auth = auth }, notice, CancellationToken.None));

            var items = asked.ToArray();
            Assert.Equal("https://ntfy.example.com/", items[0].Url);
            Assert.Contains("\"topic\":\"my-topic\"", items[0].Body, StringComparison.Ordinal);
            Assert.Contains("Bearer tk_1", items[0].Headers, StringComparison.Ordinal);
            Assert.Equal("https://gotify.example.com/message", items[1].Url);
            Assert.Contains("X-Gotify-Key: app", items[1].Headers, StringComparison.Ordinal);
            Assert.Contains("vapid t=", items[2].Headers, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Overdue_loans_are_told_once_a_day()
    {
        var owner = await factory.SignUpAsync("Due Owner");
        var ownerId = await factory.ReaderIdAsync("Due Owner");
        var friendId = (await factory.SignUpAsync("Due Friend")) is not null ? await factory.ReaderIdAsync("Due Friend") : 0;
        var notices = factory.Services.GetRequiredService<Notices>();
        await using (var scope = Scope(ownerId))
        {
            await notices.SetNtfyAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), "https://ntfy.sh/due-owner", null);
        }

        var book = (await (await owner.PostAsJsonAsync("/books", new CreateBookRequest("Overdue", "Someone", BookStatus.Finished, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        await owner.PostAsJsonAsync($"/books/{book.Id}/lend", new LendRequest(friendId, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2)), JsonOptions);

        Assert.True(await notices.RemindDueAsync(CancellationToken.None) >= 1);
        var told = Assert.Single(factory.Channels.Sent, sent => sent.Address == "https://ntfy.sh/due-owner");
        Assert.Equal(("Your loans on Shelf", "1 book you lent is overdue."), (told.Notice.Title, told.Notice.Body));
        await notices.RemindDueAsync(CancellationToken.None);
        Assert.Single(factory.Channels.Sent, sent => sent.Address == "https://ntfy.sh/due-owner");
    }

    [Fact]
    public async Task Notifications_go_to_the_address_a_reader_signs_in_with_until_they_turn_it_off()
    {
        var owner = await factory.SignUpAsync("Mailed Owner");
        var friend = await factory.SignUpAsync("Mailed Friend");
        var friendId = await factory.ReaderIdAsync("Mailed Friend");
        var address = ShelfApiFactory.EmailOf("Mailed Friend");
        var book = (await (await owner.PostAsJsonAsync("/books", new CreateBookRequest("Persuasion", "Jane Austen", BookStatus.Finished, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;

        await owner.PostAsJsonAsync($"/books/{book.Id}/lend", new LendRequest(friendId), JsonOptions);
        await WaitIdleAsync();
        var lent = Assert.Single(factory.Mail.Sent, mail => mail.To == address);
        Assert.Equal("Mailed Owner lent you a book", lent.Subject);
        Assert.StartsWith("Hello Mailed Friend,", lent.Body);
        Assert.Contains("Persuasion", lent.Body);

        await using (var scope = Scope(friendId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            Assert.True((await Notices.SettingsAsync(db)).EmailNotices);
            await Notices.SetEmailNoticesAsync(db, false);
        }

        await owner.PostAsync($"/books/{book.Id}/return", null);
        await owner.PostAsJsonAsync($"/books/{book.Id}/lend", new LendRequest(friendId), JsonOptions);
        await WaitIdleAsync();
        Assert.Single(factory.Mail.Sent, mail => mail.To == address);
    }

    [Fact]
    public async Task Club_members_hear_of_the_next_book_a_meeting_called_off_reminders_and_being_let_go()
    {
        var owner = await factory.SignUpAsync("Club Runner");
        await factory.SignUpAsync("Club Reader");
        var readerId = await factory.ReaderIdAsync("Club Reader");
        var reader = ShelfApiFactory.EmailOf("Club Reader");
        var club = (await (await owner.PostAsJsonAsync("/books/clubs", new ClubRequest("Tuesday readers", null), JsonOptions)).Content.ReadFromJsonAsync<ClubResponse>(JsonOptions))!;
        await owner.PostAsJsonAsync($"/books/clubs/{club.Id}/members", new ClubInvite(readerId), JsonOptions);
        await owner.PostAsJsonAsync($"/books/clubs/{club.Id}/books", new ClubBookRequest(Title: "Middlemarch", Author: "George Eliot"), JsonOptions);
        await owner.PostAsJsonAsync($"/books/clubs/{club.Id}/books", new ClubBookRequest(Title: "Persuasion", Author: "Jane Austen"), JsonOptions);
        var books = (await owner.GetFromJsonAsync<ClubResponse>($"/books/clubs/{club.Id}", JsonOptions))!.Books;
        var next = books.Single(book => book.Title == "Persuasion");

        await owner.PutAsync($"/books/clubs/{club.Id}/current/{next.Id}", null);
        // The first book added became the one being read, and was told of too.
        await WaitMailAsync(reader, "Tuesday readers is reading a new book", "Middlemarch, by George Eliot");
        await WaitMailAsync(reader, "Tuesday readers is reading a new book", "Persuasion, by Jane Austen");

        var made = await (await owner.PostAsJsonAsync($"/books/clubs/{club.Id}/meetings", new ClubMeetingRequest(next.Id, DateTimeOffset.UtcNow.AddDays(3)), JsonOptions)).Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        await owner.DeleteAsync($"/books/clubs/{club.Id}/meetings/{made.GetProperty("id").GetInt32()}");
        Assert.Contains("Persuasion", (await WaitMailAsync(reader, "Tuesday readers will not meet")).Body);

        // A day ahead, then an hour ahead, each once; one arranged an hour before it is not reminded of.
        int meetingId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var meeting = new ClubMeeting { ClubId = club.Id, ClubBookId = next.Id, At = DateTimeOffset.UtcNow.AddHours(23), CreatedAt = DateTimeOffset.UtcNow.AddDays(-3) };
            db.ClubMeetings.Add(meeting);
            db.ClubMeetings.Add(new ClubMeeting { ClubId = club.Id, ClubBookId = next.Id, At = DateTimeOffset.UtcNow.AddMinutes(50), CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
            await db.SaveChangesAsync();
            meetingId = meeting.Id;
        }

        var notices = factory.Services.GetRequiredService<Notices>();
        await WaitIdleAsync();
        Assert.Equal(2, await notices.RemindMeetingsAsync(CancellationToken.None));
        Assert.StartsWith("Reminder: Tuesday readers meets", factory.Mail.Sent.Last(mail => mail.To == reader).Subject);
        Assert.Equal(0, await notices.RemindMeetingsAsync(CancellationToken.None));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            db.ClubMeetings.Single(item => item.Id == meetingId).At = DateTimeOffset.UtcNow.AddMinutes(40);
            await db.SaveChangesAsync();
        }

        Assert.Equal(2, await notices.RemindMeetingsAsync(CancellationToken.None));

        await owner.DeleteAsync($"/books/clubs/{club.Id}/members/{readerId}");
        Assert.Contains("Club Runner took you out", (await WaitMailAsync(reader, "You are no longer in Tuesday readers")).Body);

        await owner.PostAsJsonAsync($"/books/clubs/{club.Id}/members", new ClubInvite(readerId), JsonOptions);
        await owner.DeleteAsync($"/books/clubs/{club.Id}");
        Assert.Contains("Club Runner ended the book club.", (await WaitMailAsync(reader, "Tuesday readers has ended")).Body);
    }

    [Fact]
    public async Task An_ask_turned_down_is_told_to_whoever_asked()
    {
        var owner = await factory.SignUpAsync("Careful Lender");
        var friend = await factory.SignUpAsync("Hopeful Borrower");
        var book = (await (await owner.PostAsJsonAsync("/books", new CreateBookRequest("Emma", "Jane Austen", BookStatus.Finished, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        await owner.PutAsJsonAsync("/books/shelves/open", new ShelfOpenChange(true), JsonOptions);
        (await friend.PostAsync($"/books/{book.Id}/ask", null)).EnsureSuccessStatusCode();
        var ask = (await owner.GetFromJsonAsync<LoanAsks>("/books/asks", JsonOptions))!.Incoming.Single(item => item.BookId == book.Id);
        (await owner.DeleteAsync($"/books/asks/{ask.Id}")).EnsureSuccessStatusCode();
        var told = await WaitMailAsync(ShelfApiFactory.EmailOf("Hopeful Borrower"), "Careful Lender can't lend you a book");
        Assert.Contains("Emma", told.Body);
    }

    [Fact]
    public async Task Changes_to_how_a_reader_signs_in_are_emailed_to_them_and_admins_hear_of_new_readers()
    {
        var reader = await factory.SignUpAsync("Guarded Reader");
        var readerId = await factory.ReaderIdAsync("Guarded Reader");
        Assert.Contains("A new reader signed up.", (await WaitMailAsync(ShelfApiFactory.EmailOf("Tenar"), "Guarded Reader joined the shelf")).Body);

        // A new address: the old one hears of it.
        var old = ShelfApiFactory.EmailOf("Guarded Reader");
        await reader.PutAsJsonAsync("/account/email", new EmailSettingsRequest("guarded@example.org", true), JsonOptions);
        Assert.Contains("guarded@example.org", (await WaitMailAsync(old, "Your Shelf email address changed")).Body);

        // A sign-in from a device not seen before, once.
        var laptop = factory.CreateClient(new() { AllowAutoRedirect = false });
        laptop.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64; rv:130.0) Gecko/20100101 Firefox/130.0");
        await ShelfApiFactory.PostFormAsync(laptop, "/signin", "/account/signin", new() { ["email"] = "guarded@example.org", ["password"] = ShelfApiFactory.Password });
        await WaitMailAsync("guarded@example.org", "A new sign-in to your shelf");
        await ShelfApiFactory.PostFormAsync(laptop, "/signin", "/account/signin", new() { ["email"] = "guarded@example.org", ["password"] = ShelfApiFactory.Password });
        await WaitIdleAsync();
        Assert.Single(factory.Mail.Sent, mail => mail.To == "guarded@example.org" && mail.Subject == "A new sign-in to your shelf");

        // A password an admin gives.
        (await factory.Client.PostAsync($"/admin/readers/{readerId}/password", null)).EnsureSuccessStatusCode();
        await WaitMailAsync("guarded@example.org", "Your Shelf password changed");
    }

    private async Task<EmailMessage> WaitMailAsync(string to, string subject, string? saying = null)
    {
        for (var i = 0; i < 100; i++)
        {
            if (factory.Mail.Sent.LastOrDefault(mail => mail.To == to && mail.Subject.StartsWith(subject, StringComparison.Ordinal) && (saying is null || mail.Body.Contains(saying, StringComparison.Ordinal))) is { } found)
            {
                return found;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"No email \"{subject}\" went to {to}.");
    }

    private async Task WaitIdleAsync()
    {
        var events = factory.Services.GetRequiredService<ShelfEvents>();
        for (var i = 0; i < 100 && !events.Idle; i++)
        {
            await Task.Delay(50);
        }
    }

    private static (string P256dh, string Auth) BrowserKeys()
    {
        using var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = browser.ExportParameters(false).Q;
        return (WebEncoders.Base64UrlEncode([0x04, .. q.X!, .. q.Y!]), WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16)));
    }

    // What the browser does with a push message (RFC 8291), to check the shelf sealed it right.
    private static byte[] Open(byte[] message, ECDiffieHellman browser, byte[] browserKey, byte[] secret)
    {
        var salt = message[..16];
        var recordSize = BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(16));
        Assert.Equal(4096u, recordSize);
        var serverKey = message[21..(21 + message[20])];
        using var server = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = serverKey[1..33], Y = serverKey[33..65] } });
        var shared = browser.DeriveRawSecretAgreement(server.PublicKey);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, secret, [.. "WebPush: info\0"u8, .. browserKey, .. serverKey]);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, "Content-Encoding: nonce\0"u8.ToArray());
        var body = message[(21 + message[20])..];
        var plain = new byte[body.Length - 16];
        using (var aes = new AesGcm(key, 16))
        {
            aes.Decrypt(nonce, body[..^16], body[^16..], plain);
        }

        Assert.Equal(0x02, plain[^1]);
        return plain[..^1];
    }

    private AsyncServiceScope Scope(int readerId)
    {
        var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
        return scope;
    }

    private sealed class Answers(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => answer(request);
    }
}

public sealed class StubNoticeChannels : INoticeChannels
{
    public sealed record SentNotice(string Way, string Address, string? Token, Notice Notice);

    public ConcurrentQueue<SentNotice> Sent { get; } = new();

    public string PushKey => "BNotARealKeyButLongEnoughToLookLikeOne";

    public Task<bool> NtfyAsync(string url, string? token, Notice notice, string? link, CancellationToken cancellationToken)
    {
        Sent.Enqueue(new SentNotice("ntfy", url, token, notice));
        return Task.FromResult(true);
    }

    public Task<bool> GotifyAsync(string url, string token, Notice notice, string? link, CancellationToken cancellationToken)
    {
        Sent.Enqueue(new SentNotice("gotify", url, token, notice));
        return Task.FromResult(true);
    }

    // An endpoint ending in "gone" has been dropped by its push service.
    public Task<PushOutcome> PushAsync(PushSubscriptionRow subscription, Notice notice, CancellationToken cancellationToken)
    {
        Sent.Enqueue(new SentNotice("push", subscription.Endpoint, null, notice));
        return Task.FromResult(subscription.Endpoint.EndsWith("gone", StringComparison.Ordinal) ? PushOutcome.Gone : PushOutcome.Sent);
    }

    public async Task<SentNotice> WaitAsync(Func<SentNotice, bool> match)
    {
        for (var i = 0; i < 100; i++)
        {
            if (Sent.FirstOrDefault(match) is { } found)
            {
                return found;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("No such notification was sent.");
    }
}

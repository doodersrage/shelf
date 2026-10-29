using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class FreshProofTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // A new way in wants a recent sign-in to the site: a fresh one may make a token, but a token itself may not
    // make another, nor start a passkey.
    [Fact]
    public async Task A_token_or_passkey_wants_a_recent_sign_in()
    {
        var reader = await factory.SignUpAsync("Fresh Prover");
        var made = await reader.PostAsJsonAsync("/account/tokens", new NewApiTokenRequest("Script", CanChange: true), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var token = (await made.Content.ReadFromJsonAsync<NewApiToken>(JsonOptions))!.Token;

        var script = factory.CreateClient();
        script.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.False((await script.PostAsJsonAsync("/account/tokens", new NewApiTokenRequest("Another", CanChange: true), JsonOptions)).IsSuccessStatusCode);
        Assert.False((await script.PostAsync("/account/passkeys/options", null)).IsSuccessStatusCode);
    }

    // Recent means within a quarter of an hour of signing in to the site; a sign-in without the time (a token, a device
    // key, or a session from before Shelf kept it) is never recent.
    [Fact]
    public void Recent_is_a_quarter_of_an_hour()
    {
        System.Security.Claims.ClaimsPrincipal SignedIn(TimeSpan ago) => new(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(ReaderRules.SignedInClaim, DateTimeOffset.UtcNow.Subtract(ago).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))], "test"));
        Assert.True(ReaderRules.SignedInRecently(SignedIn(TimeSpan.FromMinutes(5))));
        Assert.False(ReaderRules.SignedInRecently(SignedIn(TimeSpan.FromMinutes(20))));
        Assert.False(ReaderRules.SignedInRecently(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));
    }

    // An admin's sensitive actions turn away a sign-in that isn't recent, before doing anything.
    [Fact]
    public async Task Admin_actions_that_hand_over_an_account_want_a_recent_sign_in()
    {
        async Task<object?> AsAsync(System.Security.Claims.ClaimsPrincipal user)
        {
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = user };
            var ran = false;
            var answer = await ReaderRules.RequireRecentSignIn(
                new Microsoft.AspNetCore.Http.DefaultEndpointFilterInvocationContext(http), _ => { ran = true; return ValueTask.FromResult<object?>("done"); });
            return ran ? answer : (answer as Microsoft.AspNetCore.Http.IStatusCodeHttpResult)?.StatusCode;
        }

        System.Security.Claims.ClaimsPrincipal SignedIn(TimeSpan ago) => new(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(ReaderRules.SignedInClaim, DateTimeOffset.UtcNow.Subtract(ago).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))], "test"));
        Assert.Equal("done", await AsAsync(SignedIn(TimeSpan.FromMinutes(2))));
        Assert.Equal(403, await AsAsync(SignedIn(TimeSpan.FromHours(2))));
        Assert.Equal(403, await AsAsync(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));
    }

    // A sign-in is in the activity log with its device; wrong passwords for one account in the hour are one line,
    // counted; and a flood of failures can't push the rest of the record out.
    [Fact]
    public async Task The_activity_log_keeps_sign_ins_and_folds_failures()
    {
        await factory.SignUpAsync("Logged Reader");
        var readerId = await factory.ReaderIdAsync("Logged Reader");
        var laptop = factory.CreateClient(new() { AllowAutoRedirect = false });
        laptop.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64; rv:131.0) Gecko/20100101 Firefox/131.0");
        for (var i = 0; i < 3; i++)
        {
            await ShelfApiFactory.PostFormAsync(laptop, "/signin", "/account/signin", new() { ["name"] = "Logged Reader", ["password"] = "not the password " + i });
        }

        await ShelfApiFactory.PostFormAsync(laptop, "/signin", "/account/signin", new() { ["name"] = "Logged Reader", ["password"] = ShelfApiFactory.Password });
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var lines = await Audit.RecentAsync(db, readerId);
        Assert.Equal("Firefox on Linux", lines.First(line => line.Action == "Signed in").Detail);
        Assert.Equal("×3", Assert.Single(lines, line => line.Action == "A sign-in failed: wrong password").Detail);

        // A flood of failures, with an admin's change before them: the change outlasts the trim.
        db.AuditEntries.Add(new AuditEntry { At = DateTimeOffset.UtcNow, Action = "Made an admin", ActorId = readerId, Detail = "kept" });
        await db.SaveChangesAsync();
        db.AuditEntries.AddRange(Enumerable.Range(0, Audit.KeepFailures + 50).Select(i => new AuditEntry { At = DateTimeOffset.UtcNow, Action = "A sign-in failed: wrong password", ActorId = 900_000 + i }));
        await db.SaveChangesAsync();
        await Audit.TrimAsync(db);
        Assert.True(await db.AuditEntries.AnyAsync(entry => entry.Detail == "kept"));
        Assert.True(await db.AuditEntries.CountAsync(entry => entry.Action.StartsWith("A sign-in failed")) <= Audit.KeepFailures);
    }

    // The passwords guessed first are refused, with digits and marks on the ends or without; a long phrase is fine.
    [Theory]
    [InlineData("password", true)]
    [InlineData("Password2024!", true)]
    [InlineData("12345678", true)]
    [InlineData("98765432", true)]
    [InlineData("aaaaaaaaaa", true)]
    [InlineData("qwertyuiop", true)]
    [InlineData("1q2w3e4r", true)]
    [InlineData("a long enough password", false)]
    [InlineData("correct horse battery", false)]
    [InlineData("tenar of atuan 1971", false)]
    public void Common_passwords_are_refused(string password, bool common) =>
        Assert.Equal(common ? AccountProblem.PasswordCommon : null, ReaderRules.PasswordProblem(password));

    // A passkey challenge is taken once: the same sign-in cookie sent again finds no challenge, as if it had run out.
    [Fact]
    public async Task A_passkey_challenge_is_taken_once()
    {
        var visitor = factory.CreateClient(new() { HandleCookies = false });
        var options = await visitor.PostAsync("/account/passkey/options", null);
        var cookie = options.Headers.GetValues("Set-Cookie").First(value => value.StartsWith("shelf-passkey=", StringComparison.Ordinal)).Split(';')[0];
        async Task<string> TryAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/account/passkey/signin")
            {
                Content = new StringContent("""{"credential":{"id":"AAAA","rawId":"AAAA","type":"public-key","response":{"authenticatorData":"AAAA","clientDataJSON":"AAAA","signature":"AAAA"},"clientExtensionResults":{}}}""", System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Cookie", cookie);
            return await (await visitor.SendAsync(request)).Content.ReadAsStringAsync();
        }

        var first = await TryAsync();
        Assert.True(first.Contains("does not know that passkey"), first);
        Assert.Contains("took too long", await TryAsync());
    }

    // A new password, with the other ways in ended, locks out a token and a device key; kept, they go on working.
    [Fact]
    public async Task A_new_password_can_end_every_other_way_in()
    {
        var reader = await factory.SignUpAsync("Lock Changer");
        var readerId = await factory.ReaderIdAsync("Lock Changer");
        var token = (await (await reader.PostAsJsonAsync("/account/tokens", new NewApiTokenRequest("Script", CanChange: false), JsonOptions)).Content.ReadFromJsonAsync<NewApiToken>(JsonOptions))!.Token;
        string key;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            key = await ReaderRules.NewKeyAsync(db, readerId);
            Assert.Null(await ReaderRules.ChangePasswordAsync(db, readerId, ShelfApiFactory.Password, "a kept new password 1", endOthers: false));
        }

        var script = factory.CreateClient();
        script.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var device = factory.CreateClient();
        device.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"kobo:{key}")));
        Assert.Equal(HttpStatusCode.OK, (await script.GetAsync("/books")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await device.GetAsync("/opds")).StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.Null(await ReaderRules.ChangePasswordAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), readerId, "a kept new password 1", "a locking new password 2", endOthers: true));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await script.GetAsync("/books")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync("/opds")).StatusCode);
    }

    // Wrong codes are counted for the account: past five, even the right code waits, whatever address it comes from.
    [Fact]
    public async Task Wrong_codes_are_limited_for_the_account_from_any_address()
    {
        var phone = await factory.SignUpAsync("Guessed At");
        var start = await (await phone.PostAsync("/account/two-factor/start", null)).Content.ReadFromJsonAsync<TwoFactorStart>(JsonOptions);
        string Now() => TwoFactor.Code(start!.Secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        await phone.PostAsJsonAsync("/account/two-factor/confirm", new TwoFactorCodeRequest(Now()), JsonOptions);

        async Task<string?> TryAsync(string code)
        {
            var laptop = factory.CreateClient(new() { AllowAutoRedirect = false });
            await ShelfApiFactory.PostFormAsync(laptop, "/signin", "/account/signin", new() { ["name"] = "Guessed At", ["password"] = ShelfApiFactory.Password });
            return (await ShelfApiFactory.PostFormAsync(laptop, "/signin/code", "/account/signin/code", new() { ["code"] = code })).Headers.Location?.OriginalString;
        }

        for (var i = 0; i < AccountEndpoints.MostWrongCodes; i++)
        {
            Assert.Contains("problem=CodeWrong", await TryAsync("000000"));
        }

        Assert.Contains("problem=CodesPaused", await TryAsync(Now()));
    }
}

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

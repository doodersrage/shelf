using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class SingleSignOnTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private const string Issuer = "https://id.example.org";

    [Fact]
    public async Task Someone_new_signs_up_through_the_provider_and_comes_back_to_the_same_reader()
    {
        var provider = new FakeProvider();
        await using var app = App(provider, signUps: true);
        var client = Browser(app);

        provider.Person = new("subject-lena", "lena", "lena@example.org", EmailVerified: true);
        Assert.Equal("/", await SignInAsync(client, provider));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/books")).StatusCode);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var lena = await db.Readers.SingleAsync(reader => reader.OidcSubject == $"{Issuer}|subject-lena");
            Assert.Equal(("lena", "lena@example.org", true), (lena.Name, lena.Email, lena.PasswordUnknown));
        }

        // Signed out and back in: the same reader, not a second one.
        await ShelfApiFactory.PostFormAsync(client, "/", "/account/signout", []);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/books")).StatusCode);
        Assert.Equal("/", await SignInAsync(client, provider));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            Assert.Equal(1, await db.Readers.CountAsync(reader => reader.OidcSubject == $"{Issuer}|subject-lena"));
        }

        // Disconnecting is refused while single sign-on is the only way in; once a password is set, it goes.
        Assert.Equal($"/account?problem={AccountProblem.SsoLastWay}#sso", (await PostFormAsync(client, "/account", "/account/sso/unlink")).Headers.Location?.OriginalString);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var lena = await db.Readers.SingleAsync(reader => reader.OidcSubject == $"{Issuer}|subject-lena");
            Assert.Null(await ReaderRules.ChangePasswordAsync(db, lena.Id, null, "a password of my own"));
        }

        Assert.Equal("/", await SignInAsync(client, provider));
        Assert.Equal("/account#sso", (await PostFormAsync(client, "/account", "/account/sso/unlink")).Headers.Location?.OriginalString);
        Assert.NotNull((await ShelfApiFactory.PostFormAsync(Browser(app), "/signin", "/account/signin", new() { ["name"] = "lena", ["password"] = "a password of my own" })).Headers.Location);

        // Someone else whose provider name is taken here gets a name of their own, and an unverified email is left off.
        var other = Browser(app);
        provider.Person = new("subject-other-lena", "lena", "other@example.org", EmailVerified: false);
        Assert.Equal("/", await SignInAsync(other, provider));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var second = await db.Readers.SingleAsync(reader => reader.OidcSubject == $"{Issuer}|subject-other-lena");
            Assert.Equal(("lena 2", null), (second.Name, second.Email));
        }
    }

    [Fact]
    public async Task A_reader_connects_their_account_and_strangers_are_turned_away_when_sign_ups_are_closed()
    {
        var provider = new FakeProvider();
        await using var app = App(provider, signUps: false);
        var client = Browser(app);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await ReaderRules.CreateAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), "Connecting Reader", ShelfApiFactory.Password);
        }

        await ShelfApiFactory.PostFormAsync(client, "/signin", "/account/signin", new() { ["name"] = "Connecting Reader", ["password"] = ShelfApiFactory.Password });

        // Connected from Account while signed in.
        provider.Person = new("subject-connect", "someone-else-entirely", null, EmailVerified: false);
        var linked = await FollowProviderAsync(client, provider, await client.PostAsync("/account/sso/link", null));
        Assert.Equal("/account#sso", linked);
        await ShelfApiFactory.PostFormAsync(client, "/", "/account/signout", []);
        Assert.Equal("/", await SignInAsync(client, provider));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/books")).StatusCode);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var reader = await db.Readers.SingleAsync(item => item.OidcSubject == $"{Issuer}|subject-connect");
            Assert.Equal("Connecting Reader", reader.Name);
            Assert.False(reader.PasswordUnknown);
        }

        // Nobody here has this account, and the shelf takes no new ones.
        var stranger = Browser(app);
        provider.Person = new("subject-stranger", "stranger", null, EmailVerified: false);
        Assert.Equal($"/signin?problem={AccountProblem.SsoUnknown}", await SignInAsync(stranger, provider));
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.GetAsync("/books")).StatusCode);

        // Another reader may not connect the account the first one has.
        var other = Browser(app);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            await ReaderRules.CreateAsync(db, "Another Reader", ShelfApiFactory.Password);
        }

        await ShelfApiFactory.PostFormAsync(other, "/signin", "/account/signin", new() { ["name"] = "Another Reader", ["password"] = ShelfApiFactory.Password });
        provider.Person = new("subject-connect", "someone-else-entirely", null, EmailVerified: false);
        Assert.Equal($"/account?problem={AccountProblem.SsoTaken}#sso", await FollowProviderAsync(other, provider, await other.PostAsync("/account/sso/link", null)));
    }

    [Fact]
    public async Task A_made_up_answer_from_the_provider_signs_no_one_in()
    {
        var provider = new FakeProvider();
        await using var app = App(provider, signUps: true);
        var client = Browser(app);
        provider.Person = new("subject-forged", "forger", null, EmailVerified: false);
        provider.SignWithAnotherKey = true;
        var started = await client.GetAsync("/account/sso");
        var query = HttpUtility.ParseQueryString(started.Headers.Location!.Query);
        provider.Nonce = query["nonce"];
        var back = await client.GetAsync($"/signin-oidc?code=a-code&state={Uri.EscapeDataString(query["state"]!)}");
        Assert.Equal($"/signin?problem={AccountProblem.SsoFailed}", back.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/books")).StatusCode);
    }

    private WebApplicationFactoryHost App(FakeProvider provider, bool signUps) => new(factory.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Oidc:Authority", Issuer);
        builder.UseSetting("Oidc:ClientId", "shelf");
        builder.UseSetting("Oidc:ClientSecret", "a-client-secret");
        builder.UseSetting("Oidc:Name", "Example ID");
        builder.UseSetting("Accounts:AllowSignUp", signUps ? "true" : "false");
        builder.ConfigureTestServices(services =>
            services.Configure<OpenIdConnectOptions>(SingleSignOn.Scheme, options => options.BackchannelHttpHandler = provider));
    }));

    private static Task<HttpResponseMessage> PostFormAsync(HttpClient client, string page, string action) =>
        ShelfApiFactory.PostFormAsync(client, page, action, []);

    private static HttpClient Browser(WebApplicationFactoryHost app) => app.Factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });

    // Starts at the sign-in button and follows the provider's answer back. Returns where the shelf sends the reader.
    private static async Task<string?> SignInAsync(HttpClient client, FakeProvider provider) =>
        await FollowProviderAsync(client, provider, await client.GetAsync("/account/sso"));

    private static async Task<string?> FollowProviderAsync(HttpClient client, FakeProvider provider, HttpResponseMessage started)
    {
        Assert.Equal(HttpStatusCode.Redirect, started.StatusCode);
        var authorize = started.Headers.Location!;
        Assert.StartsWith($"{Issuer}/authorize", authorize.ToString());
        var query = HttpUtility.ParseQueryString(authorize.Query);
        Assert.Equal("code", query["response_type"]);
        // Query is the code flow's own default, so it may go unsaid; it must never be form_post.
        Assert.True(query["response_mode"] is null or "query");
        Assert.Equal("S256", query["code_challenge_method"]);
        provider.Nonce = query["nonce"];

        // The provider sends the browser back with a code; the shelf trades it for the person's details.
        var callback = await client.GetAsync($"/signin-oidc?code=a-code&state={Uri.EscapeDataString(query["state"]!)}");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/account/sso/done", callback.Headers.Location?.OriginalString);
        var done = await client.GetAsync("/account/sso/done");
        return done.Headers.Location?.OriginalString;
    }

    public sealed record Person(string Subject, string Username, string? Email, bool EmailVerified);

    public sealed class WebApplicationFactoryHost(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory) : IAsyncDisposable
    {
        public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory { get; } = factory;

        public IServiceProvider Services => Factory.Services;

        public ValueTask DisposeAsync() => Factory.DisposeAsync();
    }

    // An OpenID Connect provider in miniature: discovery, its signing key, the token endpoint, and userinfo.
    private sealed class FakeProvider : HttpMessageHandler
    {
        private readonly RsaSecurityKey key = new(RSA.Create(2048)) { KeyId = "key-1" };
        private readonly RsaSecurityKey impostor = new(RSA.Create(2048)) { KeyId = "key-1" };

        public Person Person { get; set; } = new("nobody", "nobody", null, false);

        public string? Nonce { get; set; }

        public bool SignWithAnotherKey { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            object body = path switch
            {
                "/.well-known/openid-configuration" => new Dictionary<string, object>
                {
                    ["issuer"] = Issuer,
                    ["authorization_endpoint"] = $"{Issuer}/authorize",
                    ["token_endpoint"] = $"{Issuer}/token",
                    ["userinfo_endpoint"] = $"{Issuer}/userinfo",
                    ["jwks_uri"] = $"{Issuer}/jwks",
                    ["response_types_supported"] = new[] { "code" },
                    ["subject_types_supported"] = new[] { "public" },
                    ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
                    ["code_challenge_methods_supported"] = new[] { "S256" },
                },
                "/jwks" => new { keys = new[] { JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(key.Rsa.ExportParameters(false)) { KeyId = key.KeyId }) } },
                "/token" => new Dictionary<string, object>
                {
                    ["access_token"] = "an-access-token",
                    ["token_type"] = "Bearer",
                    ["expires_in"] = 300,
                    ["id_token"] = IdToken(),
                },
                "/userinfo" => Claims(),
                _ => throw new InvalidOperationException($"The provider has no {path}."),
            };
            var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }

        private Dictionary<string, object> Claims()
        {
            var claims = new Dictionary<string, object> { ["sub"] = Person.Subject, ["preferred_username"] = Person.Username };
            if (Person.Email is { } email)
            {
                claims["email"] = email;
                claims["email_verified"] = Person.EmailVerified;
            }

            return claims;
        }

        // Like many providers, the token says only who it is; the rest comes from userinfo.
        private string IdToken()
        {
            var claims = new Dictionary<string, object> { ["sub"] = Person.Subject, ["nonce"] = Nonce ?? "" };
            return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = "shelf",
                IssuedAt = DateTime.UtcNow,
                Expires = DateTime.UtcNow.AddMinutes(5),
                Claims = claims,
                SigningCredentials = new SigningCredentials(SignWithAnotherKey ? impostor : key, SecurityAlgorithms.RsaSha256),
            });
        }
    }
}

using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class TurnstileTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static IConfiguration Settings(string? hostnames = "shelf.example.org", string? publicAddress = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Turnstile:SiteKey"] = "0x4AAAAAAAsite",
            ["Turnstile:SecretKey"] = "0x4AAAAAAAsecret",
            ["Turnstile:Hostnames"] = hostnames,
            ["Email:PublicAddress"] = publicAddress,
        }).Build();

    private static (Turnstile Turnstile, Cloudflare Cloudflare) Make(IConfiguration settings, Func<HttpResponseMessage> answer)
    {
        var cloudflare = new Cloudflare(answer);
        return (new Turnstile(new HttpClient(cloudflare), settings, NullLogger<Turnstile>.Instance), cloudflare);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task A_token_passes_only_when_Cloudflare_vouches_for_it_for_this_form_on_this_shelf()
    {
        var good = """{"success":true,"action":"signin","hostname":"shelf.example.org"}""";
        var (turnstile, cloudflare) = Make(Settings(), () => Json(good));
        Assert.True(turnstile.Enabled);
        Assert.True(await turnstile.VerifyAsync("token", TurnstileActions.SignIn, "203.0.113.9", CancellationToken.None));
        Assert.Contains("secret=0x4AAAAAAAsecret", cloudflare.Sent);
        Assert.Contains("response=token", cloudflare.Sent);
        Assert.Contains("remoteip=203.0.113.9", cloudflare.Sent);

        // Made for another form, or on another site.
        Assert.False(await turnstile.VerifyAsync("token", TurnstileActions.SignUp, null, CancellationToken.None));
        Assert.Equal(TurnstileTrouble.Action, Turnstile.Problem?.Trouble);
        Assert.False(await Make(Settings(), () => Json("""{"success":true,"action":"signin","hostname":"evil.example"}""")).Turnstile.VerifyAsync("token", "signin", null, CancellationToken.None));

        // Each refusal is kept for Server health, saying why, until a check passes.
        Assert.Equal(new { Trouble = TurnstileTrouble.Hostname, Detail = "evil.example" }, new { Turnstile.Problem!.Trouble, Turnstile.Problem.Detail });
        Assert.False(await Make(Settings(), () => Json("""{"success":false,"error-codes":["invalid-input-secret"]}""")).Turnstile.VerifyAsync("token", "signin", null, CancellationToken.None));
        Assert.Equal(TurnstileTrouble.Secret, Turnstile.Problem?.Trouble);
        Assert.True(await turnstile.VerifyAsync("token", TurnstileActions.SignIn, null, CancellationToken.None));
        Assert.Null(Turnstile.Problem);

        // Turned down, an error, or no answer at all: no.
        Assert.False(await Make(Settings(), () => Json("""{"success":false,"error-codes":["timeout-or-duplicate"]}""")).Turnstile.VerifyAsync("token", "signin", null, CancellationToken.None));
        Assert.False(await Make(Settings(), () => Json("{}", HttpStatusCode.InternalServerError)).Turnstile.VerifyAsync("token", "signin", null, CancellationToken.None));
        Assert.False(await Make(Settings(), () => throw new HttpRequestException("unreachable")).Turnstile.VerifyAsync("token", "signin", null, CancellationToken.None));
        Assert.Equal(TurnstileTrouble.Unreachable, Turnstile.Problem?.Trouble);
        Assert.False(await Make(Settings(), () => Json("not json")).Turnstile.VerifyAsync("token", "signin", null, CancellationToken.None));

        // No token, or one too long to be real, is never sent.
        var (unasked, untouched) = Make(Settings(), () => Json(good));
        Assert.False(await unasked.VerifyAsync(null, "signin", null, CancellationToken.None));
        Assert.False(await unasked.VerifyAsync(new string('x', 2049), "signin", null, CancellationToken.None));
        Assert.Equal("", untouched.Sent);
    }

    [Fact]
    public void Turnstile_is_on_only_with_both_keys_and_an_address_to_expect()
    {
        Assert.False(Make(Settings(hostnames: null), () => Json("{}")).Turnstile.Enabled);
        var fromAddress = Make(Settings(hostnames: null, publicAddress: "https://Books.Example.org"), () => Json("{}")).Turnstile;
        Assert.True(fromAddress.Enabled);
        Assert.Equal(["books.example.org"], Turnstile.Hostnames(Settings(hostnames: null, publicAddress: "https://Books.Example.org")));
        Assert.Equal(["a.example", "b.example"], Turnstile.Hostnames(Settings(hostnames: "A.example, b.example")).Order());
    }

    [Fact]
    public async Task With_Turnstile_on_the_public_forms_show_it_and_go_through_only_with_a_good_token()
    {
        await using var guarded = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<ITurnstile, StubTurnstile>()));
        var visitor = guarded.CreateClient(new() { AllowAutoRedirect = false });
        var page = await visitor.GetStringAsync("/signin");
        Assert.Contains("class=\"cf-turnstile\" data-sitekey=\"0x4AAAAAAAsite\" data-action=\"signin\"", page);
        Assert.Contains("data-action=\"signup\"", await visitor.GetStringAsync("/signup"));
        Assert.Contains("data-action=\"forgot\"", await visitor.GetStringAsync("/forgot"));

        var fields = new Dictionary<string, string> { ["email"] = ShelfApiFactory.EmailOf("Tenar"), ["password"] = ShelfApiFactory.Password };
        Assert.Contains("problem=NotHuman", (await ShelfApiFactory.PostFormAsync(visitor, "/signin", "/account/signin", new(fields))).Headers.Location?.OriginalString);
        Assert.Contains("problem=NotHuman", (await ShelfApiFactory.PostFormAsync(visitor, "/signin", "/account/signin", new(fields) { [TurnstileActions.Field] = "good-for-signup" })).Headers.Location?.OriginalString);
        Assert.Equal("/", (await ShelfApiFactory.PostFormAsync(visitor, "/signin", "/account/signin", new(fields) { [TurnstileActions.Field] = "good-for-signin" })).Headers.Location?.OriginalString);

        var signUp = await ShelfApiFactory.PostFormAsync(guarded.CreateClient(new() { AllowAutoRedirect = false }), "/signin", "/account/signup", new()
        {
            ["name"] = "Script",
            ["email"] = "script@example.test",
            ["password"] = ShelfApiFactory.Password,
            ["confirm"] = ShelfApiFactory.Password,
        });
        Assert.Contains("problem=NotHuman", signUp.Headers.Location?.OriginalString);
        Assert.Equal("/forgot?problem=NotHuman", (await ShelfApiFactory.PostFormAsync(visitor, "/signin", "/account/forgot", new() { ["who"] = "anyone@example.test" })).Headers.Location?.OriginalString);

        // Off, the forms are as they were.
        Assert.DoesNotContain("cf-turnstile", await factory.CreateClient().GetStringAsync("/signin"));
    }

    // Says yes to "good-for-" and the form's own action, as Cloudflare would to a token made for that form.
    private sealed class StubTurnstile : ITurnstile
    {
        public bool Enabled => true;

        public string? SiteKey => "0x4AAAAAAAsite";

        public Task<bool> VerifyAsync(string? token, string action, string? remoteIp, CancellationToken cancellationToken) =>
            Task.FromResult(token == $"good-for-{action}");
    }

    // Cloudflare's siteverify, answering as told and keeping what it was sent.
    private sealed class Cloudflare(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        public string Sent { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://challenges.cloudflare.com/turnstile/v0/siteverify", request.RequestUri?.ToString());
            Sent = await request.Content!.ReadAsStringAsync(cancellationToken);
            return answer();
        }
    }
}

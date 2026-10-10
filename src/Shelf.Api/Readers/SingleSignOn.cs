using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Readers;

// Signing in through an OpenID Connect provider, such as Authentik, Authelia, Keycloak, or Pocket ID. A reader is
// matched by the provider's issuer and subject, the one thing it promises never to give another person; an email
// address is not trusted for that. A reader already here connects their account from Account; someone new gets an
// account of their own only while the shelf takes new accounts. The provider's own two steps stand in for Shelf's.
public static class SingleSignOn
{
    public const string Scheme = "oidc";
    public const string ExternalScheme = "shelf-external";
    private const string LinkItem = "shelf-link";
    private const string ReturnItem = "shelf-return";

    public static bool Enabled(IConfiguration configuration) =>
        configuration["Oidc:Authority"] is { Length: > 0 } && configuration["Oidc:ClientId"] is { Length: > 0 };

    // What the provider is called on the sign-in button: "Sign in with Authentik".
    public static string Name(IConfiguration configuration) =>
        configuration["Oidc:Name"] is { Length: > 0 } name ? name : "SSO";

    public static void Add(AuthenticationBuilder authentication, IConfiguration configuration)
    {
        if (!Enabled(configuration))
        {
            return;
        }

        // Holds what the provider said for the few minutes between its answer and the shelf's own sign-in.
        authentication.AddCookie(ExternalScheme, options =>
        {
            options.Cookie.Name = "shelf-sso";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
        });
        authentication.AddOpenIdConnect(Scheme, options =>
        {
            options.SignInScheme = ExternalScheme;
            options.Authority = configuration["Oidc:Authority"];
            options.ClientId = configuration["Oidc:ClientId"];
            options.ClientSecret = configuration["Oidc:ClientSecret"] is { Length: > 0 } secret ? secret : null;
            options.ResponseType = "code";
            options.UsePkce = true;
            // The answer comes back as an ordinary link rather than a form posted across sites, so the shelf's
            // short-lived cookies can be SameSite=Lax and work over plain http on a home network.
            options.ResponseMode = "query";
            options.CallbackPath = "/signin-oidc";
            options.Scope.Clear();
            foreach (var scope in (configuration["Oidc:Scopes"] ?? "openid profile email").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                options.Scope.Add(scope);
            }

            options.MapInboundClaims = false;
            options.GetClaimsFromUserInfoEndpoint = true;
            // Many providers say these only in userinfo, which keeps just the fields it is told to.
            options.ClaimActions.MapUniqueJsonKey("preferred_username", "preferred_username");
            options.ClaimActions.MapUniqueJsonKey("email_verified", "email_verified");
            options.SaveTokens = false;
            options.RequireHttpsMetadata = configuration.GetValue("Oidc:RequireHttpsMetadata", true);
            options.CorrelationCookie.SameSite = SameSiteMode.Lax;
            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.NonceCookie.SameSite = SameSiteMode.Lax;
            options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.Events.OnRemoteFailure = context =>
            {
                context.Response.Redirect($"/signin?problem={AccountProblem.SsoFailed}");
                context.HandleResponse();
                return Task.CompletedTask;
            };
        });
    }

    public static void Map(RouteGroupBuilder account, string limit)
    {
        account.MapGet("/sso", Start).AllowAnonymous().RequireRateLimiting(limit);
        account.MapGet("/sso/done", Done).AllowAnonymous().RequireRateLimiting(limit);
        account.MapPost("/sso/link", Link).RequireAuthorization();
        account.MapPost("/sso/unlink", Unlink).RequireAuthorization();
    }

    private static IResult Start([FromQuery] string? returnUrl, IConfiguration configuration)
    {
        if (!Enabled(configuration))
        {
            return TypedResults.NotFound();
        }

        var properties = new AuthenticationProperties { RedirectUri = "/account/sso/done" };
        properties.Items[ReturnItem] = ReaderRules.IsLocalUrl(returnUrl) ? returnUrl : "/";
        return TypedResults.Challenge(properties, [Scheme]);
    }

    // From Account, signed in: connect this reader to the provider's account they are about to sign in with.
    private static IResult Link(ShelfReader reader, IConfiguration configuration)
    {
        if (!Enabled(configuration) || reader.Id is not int id)
        {
            return TypedResults.NotFound();
        }

        var properties = new AuthenticationProperties { RedirectUri = "/account/sso/done" };
        properties.Items[LinkItem] = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        properties.Items[ReturnItem] = "/account";
        return TypedResults.Challenge(properties, [Scheme]);
    }

    private static async Task<IResult> Unlink(ShelfReader current, ShelfDb db, CancellationToken cancellationToken)
    {
        var reader = await db.Readers.FirstOrDefaultAsync(item => item.Id == current.Id, cancellationToken);
        if (reader is null)
        {
            return TypedResults.NotFound();
        }

        if (reader.PasswordUnknown)
        {
            return TypedResults.Redirect($"/account?problem={AccountProblem.SsoLastWay}#sso");
        }

        reader.OidcSubject = null;
        await db.SaveChangesAsync(cancellationToken);
        await Audit.NoteAsync(db, Say("Disconnected single sign-on"), reader, cancellationToken: cancellationToken);
        return TypedResults.Redirect("/account#sso");
    }

    private static async Task<IResult> Done(HttpContext http, ShelfDb db, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var result = await http.AuthenticateAsync(ExternalScheme);
        await http.SignOutAsync(ExternalScheme);
        if (!result.Succeeded || result.Principal.FindFirst("sub") is not { Value.Length: > 0 } sub)
        {
            return TypedResults.Redirect($"/signin?problem={AccountProblem.SsoFailed}");
        }

        var who = result.Principal;
        var subject = $"{sub.Issuer}|{sub.Value}";
        var back = result.Properties?.Items.TryGetValue(ReturnItem, out var wanted) == true && ReaderRules.IsLocalUrl(wanted) ? wanted! : "/";

        // Connecting, from Account: only for the reader who asked, while still signed in as them.
        if (result.Properties?.Items.TryGetValue(LinkItem, out var linking) == true)
        {
            var signedIn = ShelfReader.IdOf(http.User);
            var reader = signedIn?.ToString(System.Globalization.CultureInfo.InvariantCulture) == linking
                ? await db.Readers.FirstOrDefaultAsync(item => item.Id == signedIn, cancellationToken)
                : null;
            if (reader is null)
            {
                return TypedResults.Redirect($"/signin?problem={AccountProblem.SsoFailed}");
            }

            if (await db.Readers.AnyAsync(item => item.OidcSubject == subject && item.Id != reader.Id, cancellationToken))
            {
                return TypedResults.Redirect($"/account?problem={AccountProblem.SsoTaken}#sso");
            }

            reader.OidcSubject = subject;
            await db.SaveChangesAsync(cancellationToken);
            await Audit.NoteAsync(db, Say("Connected single sign-on"), reader, cancellationToken: cancellationToken);
            return TypedResults.Redirect("/account#sso");
        }

        var known = await db.Readers.FirstOrDefaultAsync(item => item.OidcSubject == subject, cancellationToken);
        if (known is not null)
        {
            await AccountEndpoints.SignInAsync(http, db, known, cancellationToken);
            await Audit.NoteAsync(db, Say("Signed in with single sign-on"), known, cancellationToken: cancellationToken);
            return TypedResults.Redirect(back);
        }

        var open = AccountEndpoints.SignUpOpen(configuration) || configuration.GetValue("Oidc:CreateAccounts", false)
            || !await db.Readers.AnyAsync(cancellationToken);
        if (!open)
        {
            return TypedResults.Redirect($"/signin?problem={AccountProblem.SsoUnknown}");
        }

        var created = await CreateAsync(db, who, subject, cancellationToken);
        if (created is null)
        {
            return TypedResults.Redirect($"/signin?problem={AccountProblem.SsoFailed}");
        }

        await AccountEndpoints.SignInAsync(http, db, created, cancellationToken);
        return TypedResults.Redirect(back);
    }

    // A new reader, named as the provider calls them (made unique here), with a password nobody knows.
    private static async Task<Reader?> CreateAsync(ShelfDb db, ClaimsPrincipal who, string subject, CancellationToken cancellationToken)
    {
        var email = who.FindFirst("email")?.Value;
        var verified = string.Equals(who.FindFirst("email_verified")?.Value, "true", StringComparison.OrdinalIgnoreCase);
        var wanted = new[] { who.FindFirst("preferred_username")?.Value, who.FindFirst("name")?.Value, email?.Split('@')[0], "Reader" }
            .Select(ReaderRules.Clean)
            .Select(name => name.Length > ReaderRules.MaxNameLength - 4 ? name[..(ReaderRules.MaxNameLength - 4)].Trim() : name)
            .First(name => name.Length > 0 && ReaderRules.NameProblem(name) is null);
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        for (var n = 1; n < 100; n++)
        {
            var name = n == 1 ? wanted : $"{wanted} {n}";
            var (reader, problem) = await ReaderRules.CreateAsync(db, name, password, cancellationToken);
            if (problem == AccountProblem.NameTaken)
            {
                continue;
            }

            if (reader is null)
            {
                return null;
            }

            reader.OidcSubject = subject;
            reader.PasswordUnknown = true;
            if (verified && EmailRules.CleanAddress(email) is { Length: > 0 } address)
            {
                reader.Email = address;
            }

            await db.SaveChangesAsync(cancellationToken);
            await Audit.NoteAsync(db, Say("Signed up with single sign-on"), reader, cancellationToken: cancellationToken);
            return reader;
        }

        return null;
    }
}

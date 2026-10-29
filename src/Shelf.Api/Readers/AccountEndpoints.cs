using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Shelf.Api.Books;
using Shelf.Api.Data;

namespace Shelf.Api.Readers;

public static class AccountEndpoints
{
    public const string SignInLimit = "sign-in";
    private const string PendingCookie = "shelf-pending";
    private static readonly TimeSpan PendingLasts = TimeSpan.FromMinutes(5);

    public static void MapAccounts(this IEndpointRouteBuilder app)
    {
        var account = app.MapGroup("/account").WithTags("Account");
        account.MapPost("/signin", SignIn).AllowAnonymous().RequireRateLimiting(SignInLimit);
        account.MapPost("/signin/code", SignInCode).AllowAnonymous().RequireRateLimiting(SignInLimit);
        account.MapGet("/sessions", Sessions).RequireAuthorization();
        account.MapDelete("/sessions/{id}", EndSession).RequireAuthorization();
        account.MapPost("/two-factor/start", StartTwoFactor).RequireAuthorization().RequireRateLimiting(SignInLimit);
        account.MapPost("/two-factor/confirm", ConfirmTwoFactor).RequireAuthorization().RequireRateLimiting(SignInLimit);
        account.MapPost("/two-factor/disable", DisableTwoFactor).RequireAuthorization().RequireRateLimiting(SignInLimit);
        Passkeys.Map(account, SignInLimit);
        ApiTokens.Map(account);
        SingleSignOn.Map(account, SignInLimit);
        account.MapPost("/signup", SignUp).AllowAnonymous().RequireRateLimiting(SignInLimit);
        account.MapPost("/signout", SignOut).AllowAnonymous();
        account.MapPost("/remove", RemoveSelf).RequireAuthorization().RequireRateLimiting(SignInLimit);
        account.MapPost("/forgot", Forgot).AllowAnonymous().RequireRateLimiting(SignInLimit);
        account.MapPost("/reset", Reset).AllowAnonymous().RequireRateLimiting(SignInLimit);
        account.MapPut("/email", SetEmail).RequireAuthorization();
        account.MapGet("/email/confirm", ConfirmEmail).AllowAnonymous().RequireRateLimiting(SignInLimit);
        account.MapPost("/email/test", TestEmail).RequireAuthorization();
        account.MapPut("/kindle", SetKindle).RequireAuthorization();

        app.MapGet("/readers", Others).WithTags("Account").RequireAuthorization();
    }

    // By email address, or by name for a reader without one; a form that still sends "name" is answered the same.
    private static async Task<RedirectHttpResult> SignIn(
        [FromForm] string? email,
        [FromForm] string? name,
        [FromForm] string? password,
        [FromForm] string? returnUrl,
        [FromForm(Name = TurnstileActions.Field)] string? turnstile,
        HttpContext http,
        ShelfDb db,
        IDataProtectionProvider protection,
        ITurnstile turnstiles,
        CancellationToken cancellationToken)
    {
        var who = string.IsNullOrWhiteSpace(email) ? name : email;
        if (!await HumanAsync(turnstiles, turnstile, TurnstileActions.SignIn, http, cancellationToken))
        {
            return TypedResults.Redirect(Back("/signin", AccountProblem.NotHuman, null, returnUrl, who));
        }

        var reader = await ReaderRules.VerifyAsync(db, who, password, cancellationToken);
        if (reader is null)
        {
            return TypedResults.Redirect(Back("/signin", AccountProblem.WrongPassword, null, returnUrl, who));
        }

        if (reader.Suspended)
        {
            return TypedResults.Redirect(Back("/signin", AccountProblem.Suspended, null, returnUrl, who));
        }

        if (reader.TwoFactorEnabled)
        {
            // The password was right; the second step happens on the next page, for the next five minutes.
            var pending = Pending(protection).Protect($"{reader.Id}|{reader.Stamp}", PendingLasts);
            http.Response.Cookies.Append(PendingCookie, pending, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = http.Request.IsHttps,
                MaxAge = PendingLasts,
                Path = "/",
            });
            return TypedResults.Redirect(ReaderRules.IsLocalUrl(returnUrl) ? $"/signin/code?returnUrl={Uri.EscapeDataString(returnUrl!)}" : "/signin/code");
        }

        await SignInAsync(http, db, reader, cancellationToken);
        return TypedResults.Redirect(ReaderRules.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }

    // Wrong codes for one account, from any address: past five in a quarter of an hour, no more are tried for it until
    // the quarter is up, so changing address doesn't buy more guesses.
    public const int MostWrongCodes = 5;
    private static readonly TimeSpan WrongCodeWindow = TimeSpan.FromMinutes(15);

    private static List<DateTimeOffset> WrongCodes(IMemoryCache cache, int readerId) =>
        cache.GetOrCreate($"wrong-codes:{readerId}", entry =>
        {
            entry.SlidingExpiration = WrongCodeWindow;
            return new List<DateTimeOffset>();
        })!;

    private static async Task<RedirectHttpResult> SignInCode(
        [FromForm] string? code,
        [FromForm] string? returnUrl,
        HttpContext http,
        ShelfDb db,
        IDataProtectionProvider protection,
        IMemoryCache cache,
        CancellationToken cancellationToken)
    {
        Reader? reader = null;
        try
        {
            var parts = http.Request.Cookies[PendingCookie] is { } cookie ? Pending(protection).Unprotect(cookie).Split('|') : [];
            if (parts.Length == 2 && int.TryParse(parts[0], out var readerId))
            {
                reader = await db.Readers.FirstOrDefaultAsync(item => item.Id == readerId && item.Stamp == parts[1], cancellationToken);
            }
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
        }

        if (reader is null)
        {
            return TypedResults.Redirect(Back("/signin", AccountProblem.WrongPassword, null, returnUrl));
        }

        var wrong = WrongCodes(cache, reader.Id);
        bool tooMany;
        lock (wrong)
        {
            wrong.RemoveAll(at => DateTimeOffset.UtcNow - at > WrongCodeWindow);
            tooMany = wrong.Count >= MostWrongCodes;
        }

        if (tooMany || !await TwoFactor.CheckAsync(db, protection, reader, code, cancellationToken))
        {
            lock (wrong)
            {
                wrong.Add(DateTimeOffset.UtcNow);
            }

            // Kept, so a reader can see someone has their password and is trying codes.
            await Audit.NoteAsync(db, Localization.Words.Say("A sign-in failed: wrong code"), reader, cancellationToken: cancellationToken);
            var again = new Dictionary<string, string?> { ["problem"] = tooMany ? nameof(AccountProblem.CodesPaused) : nameof(AccountProblem.CodeWrong) };
            if (ReaderRules.IsLocalUrl(returnUrl))
            {
                again["returnUrl"] = returnUrl;
            }

            return TypedResults.Redirect(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString("/signin/code", again));
        }

        http.Response.Cookies.Delete(PendingCookie);
        await SignInAsync(http, db, reader, cancellationToken);
        return TypedResults.Redirect(ReaderRules.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }

    private static ITimeLimitedDataProtector Pending(IDataProtectionProvider protection) =>
        protection.CreateProtector("Shelf.SignIn.Pending").ToTimeLimitedDataProtector();

    private static async Task<Ok<SessionSummary[]>> Sessions(HttpContext http, ShelfDb db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await TwoFactor.SessionsAsync(db, db.ReaderId, http.User.FindFirst(TwoFactor.SessionClaim)?.Value, cancellationToken));

    private static async Task<Results<NoContent, NotFound>> EndSession(string id, ShelfDb db, CancellationToken cancellationToken) =>
        await TwoFactor.SignOutDeviceAsync(db, db.ReaderId, id, cancellationToken) > 0 ? TypedResults.NoContent() : TypedResults.NotFound();

    private static async Task<Results<Ok<TwoFactorStart>, ValidationProblem>> StartTwoFactor(ShelfDb db, IDataProtectionProvider protection, CancellationToken cancellationToken)
    {
        var reader = await db.Readers.FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        // Starting again replaces the secret and turns two-step off: once it is on, only turning it off, with a code, may.
        if (reader.TwoFactorEnabled)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Code"] = [Localization.Words.T("Two-step sign-in is already on. Turn it off with a code first.")] });
        }

        var secret = TwoFactor.NewSecret();
        reader.TwoFactorSecret = TwoFactor.Protector(protection).Protect(secret);
        reader.TwoFactorEnabled = false;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new TwoFactorStart(secret, TwoFactor.Uri(secret, reader.Email ?? reader.Name)));
    }

    private static async Task<Results<Ok<RecoveryCodes>, ValidationProblem>> ConfirmTwoFactor(
        TwoFactorCodeRequest request,
        ShelfDb db,
        IDataProtectionProvider protection,
        CancellationToken cancellationToken)
    {
        var reader = await db.Readers.FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        if (reader.TwoFactorSecret is null
            || TwoFactor.MatchStep(TwoFactor.Protector(protection).Unprotect(reader.TwoFactorSecret), request.Code, DateTimeOffset.UtcNow) is not long step)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Code"] = [ReaderRules.Describe(AccountProblem.CodeWrong)] });
        }

        reader.TwoFactorEnabled = true;
        // The code that turned it on is taken, so it can't sign in again.
        reader.TwoFactorLastStep = step;
        await db.SaveChangesAsync(cancellationToken);
        await Audit.NoteAsync(db, Localization.Words.Say("Turned on two-step sign-in"), reader, cancellationToken: cancellationToken);
        return TypedResults.Ok(new RecoveryCodes(await TwoFactor.NewRecoveryCodesAsync(db, reader.Id, cancellationToken)));
    }

    private static async Task<Results<NoContent, ValidationProblem>> DisableTwoFactor(
        TwoFactorCodeRequest request,
        ShelfDb db,
        IDataProtectionProvider protection,
        CancellationToken cancellationToken)
    {
        var reader = await db.Readers.FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        if (!await TwoFactor.CheckAsync(db, protection, reader, request.Code, cancellationToken))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Code"] = [ReaderRules.Describe(AccountProblem.CodeWrong)] });
        }

        await TwoFactor.DisableAsync(db, reader, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<RedirectHttpResult> SignUp(
        [FromForm] string? name,
        [FromForm] string? email,
        [FromForm] string? password,
        [FromForm] string? confirm,
        [FromForm] string? returnUrl,
        [FromForm] string? invite,
        [FromForm(Name = TurnstileActions.Field)] string? turnstile,
        HttpContext http,
        ShelfDb db,
        IConfiguration configuration,
        IEmailSender mail,
        ITurnstile turnstiles,
        CancellationToken cancellationToken)
    {
        if (!await HumanAsync(turnstiles, turnstile, TurnstileActions.SignUp, http, cancellationToken))
        {
            return TypedResults.Redirect(Back("/signup", AccountProblem.NotHuman, name, returnUrl, email, invite));
        }

        // An invitation lets its address in whoever may sign up otherwise, and the address is the one it went to.
        var invitation = await Management.InvitationAsync(db, invite, cancellationToken);
        if (!string.IsNullOrEmpty(invite) && invitation is null)
        {
            return TypedResults.Redirect(Back("/signup", AccountProblem.InviteExpired, name, returnUrl, email));
        }

        email = invitation?.Email ?? email;
        if (invitation is null && await Management.SignUpModeAsync(db, configuration, cancellationToken) != SignUpMode.Open && await db.Readers.AnyAsync(cancellationToken))
        {
            return TypedResults.Redirect(Back("/signup", AccountProblem.SignUpClosed, name, returnUrl, email));
        }

        if (!string.Equals(password, confirm, StringComparison.Ordinal))
        {
            return TypedResults.Redirect(Back("/signup", AccountProblem.PasswordsDiffer, name, returnUrl, email, invite));
        }

        // Signing up gives an address, which is how the reader signs in from then on.
        if (string.IsNullOrWhiteSpace(email))
        {
            return TypedResults.Redirect(Back("/signup", AccountProblem.EmailMissing, name, returnUrl, email));
        }

        // When the shelf can, the address waits for its link, so no one can take an address that is not theirs. An
        // invitation's link went to the address already.
        var link = EmailRules.ResetAddress(configuration, http.Request);
        var confirms = invitation is null && EmailRules.Confirms(mail, link);
        var (reader, problem) = await ReaderRules.CreateAsync(db, name, email, password, cancellationToken, confirmed: !confirms);
        if (reader is null)
        {
            return TypedResults.Redirect(Back("/signup", problem ?? AccountProblem.NameMissing, name, returnUrl, email, invite));
        }

        if (invitation is not null)
        {
            await Management.AcceptAsync(db, invitation, reader, cancellationToken);
        }

        if (confirms)
        {
            try
            {
                await EmailRules.SendConfirmationAsync(db, mail, reader, link!, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The sender logged it; the account asks to send the link again.
            }
        }

        await SignInAsync(http, db, reader, cancellationToken);
        return TypedResults.Redirect(ReaderRules.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }

    private static async Task<Results<RedirectHttpResult, BadRequest>> SignOut(HttpContext http, IAntiforgery antiforgery, ShelfDb db, [FromForm] string? returnUrl)
    {
        if (!await antiforgery.IsRequestValidAsync(http))
        {
            return TypedResults.BadRequest();
        }

        if (ShelfReader.IdOf(http.User) is int readerId && http.User.FindFirst(TwoFactor.SessionClaim)?.Value is { } session)
        {
            await TwoFactor.EndSessionAsync(db, readerId, session);
        }

        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        // "Sign in again" comes back to where it was asked.
        return TypedResults.Redirect(ReaderRules.IsLocalUrl(returnUrl) ? $"/signin?returnUrl={Uri.EscapeDataString(returnUrl!)}" : "/signin");
    }

    private static async Task<Results<NoContent, ValidationProblem>> RemoveSelf(
        RemoveAccountRequest request,
        HttpContext http,
        ShelfDb db,
        EbookStore ebooks,
        AudioStore audio,
        CoverStore covers,
        CancellationToken cancellationToken)
    {
        var problem = await ReaderRules.RemoveSelfAsync(db, ebooks, audio, covers, db.ReaderId, request.Password, cancellationToken);
        if (problem is not null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.Password)] = [ReaderRules.Describe(problem.Value)],
            });
        }

        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return TypedResults.NoContent();
    }

    // The answer is the same whether or not that reader exists, so the form tells no one who has an account.
    private static async Task<RedirectHttpResult> Forgot(
        [FromForm] string? who,
        [FromForm(Name = TurnstileActions.Field)] string? turnstile,
        HttpContext http,
        ShelfDb db,
        IEmailSender email,
        IConfiguration configuration,
        ILoggerFactory loggers,
        ITurnstile turnstiles,
        CancellationToken cancellationToken)
    {
        if (!await HumanAsync(turnstiles, turnstile, TurnstileActions.Forgot, http, cancellationToken))
        {
            return TypedResults.Redirect($"/forgot?problem={AccountProblem.NotHuman}");
        }

        if (EmailRules.ResetAddress(configuration, http.Request) is { } address)
        {
            try
            {
                await EmailRules.RequestResetAsync(db, email, who, address, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The sender logged it, and Server health shows it; the page says the same either way.
            }
        }
        else if (email.Enabled)
        {
            loggers.CreateLogger("Shelf.Accounts").LogWarning("A password reset was asked for, but no email was sent: set Email:PublicAddress to the shelf's own address, so the link in it points there.");
        }

        return TypedResults.Redirect("/forgot?sent=1");
    }

    private static async Task<RedirectHttpResult> Reset(
        [FromForm] string? token,
        [FromForm] string? password,
        [FromForm] string? confirm,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        var problem = !string.Equals(password, confirm, StringComparison.Ordinal)
            ? AccountProblem.PasswordsDiffer
            : await EmailRules.ResetAsync(db, token, password, cancellationToken);
        return TypedResults.Redirect(problem is null
            ? "/signin?notice=reset"
            : Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString("/reset", new Dictionary<string, string?> { ["token"] = token, ["problem"] = problem.ToString() }));
    }

    // 202 Accepted when the new address waits for the link sent to it; 204 when it is the reader's at once.
    private static async Task<Results<NoContent, Accepted, ValidationProblem>> SetEmail(
        EmailSettingsRequest request,
        HttpContext http,
        ShelfDb db,
        IEmailSender mail,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        AccountProblem? problem;
        bool pending;
        try
        {
            (problem, pending) = await EmailRules.ChangeAsync(db, mail, db.ReaderId, request.Email, request.Reminders, EmailRules.ResetAddress(configuration, http.Request), cancellationToken,
                signedInRecently: ReaderRules.SignedInRecently(http.User));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Email)] = [Localization.Words.T("The mail server did not take it: {0}", ex.Message)] });
        }

        return problem is not null
            ? TypedResults.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Email)] = [ReaderRules.Describe(problem.Value)] })
            : pending ? TypedResults.Accepted((string?)null) : TypedResults.NoContent();
    }

    private static async Task<RedirectHttpResult> ConfirmEmail(string? token, HttpContext http, ShelfDb db, CancellationToken cancellationToken)
    {
        var problem = await EmailRules.ConfirmAsync(db, token, cancellationToken);
        var signedIn = http.User.Identity?.IsAuthenticated == true;
        return TypedResults.Redirect((problem, signedIn) switch
        {
            (null, true) => "/account?confirmed=1#email",
            (null, false) => "/signin?notice=confirmed",
            (_, true) => $"/account?problem={problem}#email",
            _ => $"/signin?problem={problem}",
        });
    }

    private static async Task<Results<NoContent, ValidationProblem>> SetKindle(KindleSettingsRequest request, ShelfDb db, CancellationToken cancellationToken)
    {
        var problem = await ReaderRules.SetKindleAsync(db, db.ReaderId, request.Email, cancellationToken);
        return problem is null
            ? TypedResults.NoContent()
            : TypedResults.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Email)] = [ReaderRules.Describe(problem.Value)] });
    }

    private static async Task<Results<NoContent, ValidationProblem>> TestEmail(ShelfDb db, IEmailSender email, CancellationToken cancellationToken)
    {
        var reader = await db.Readers.AsNoTracking().FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        if (!email.Enabled || reader.Email is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Email"] = [Localization.Words.T("Add an address, and an admin has to set up a mail server, first.")] });
        }

        try
        {
            await email.SendAsync(new EmailMessage(reader.Email, Localization.Words.T("Shelf can reach you"), Localization.Words.T("Hello {0},\n\nThis is a test from your shelf. Email works.", reader.Name)), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Email"] = [Localization.Words.T("The mail server did not take it: {0}", ex.Message)] });
        }

        return TypedResults.NoContent();
    }

    private static async Task<Ok<ReaderResponse[]>> Others(ShelfDb db, ShelfReader reader, CancellationToken cancellationToken) =>
        TypedResults.Ok(await ReaderRules.OthersAsync(db, reader.Id ?? 0, cancellationToken));

    // With Turnstile on, a public form goes through only with a token Cloudflare vouches for; with it off, always.
    private static async Task<bool> HumanAsync(ITurnstile turnstiles, string? token, string action, HttpContext http, CancellationToken cancellationToken) =>
        !turnstiles.Enabled || await turnstiles.VerifyAsync(token, action, http.Connection.RemoteIpAddress?.ToString(), cancellationToken);

    public static bool SignUpOpen(IConfiguration configuration) =>
        configuration.GetValue("Accounts:AllowSignUp", true);

    // Every sign-in is a session of its own, listed on the account page until it signs out.
    // Every sign-in to the site is kept in the activity log, with the device, so a reader can see one that wasn't them.
    internal static async Task SignInAsync(HttpContext http, ShelfDb db, Reader reader, CancellationToken cancellationToken, string? how = null)
    {
        var device = TwoFactor.DeviceName(http.Request.Headers.UserAgent);
        var session = await TwoFactor.StartSessionAsync(db, reader.Id, device, cancellationToken);
        try
        {
            await Audit.NoteAsync(db, how ?? Localization.Words.Say("Signed in"), reader, device, cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // The record of a sign-in never stops the sign-in (a busy database just after a restore, say).
            foreach (var entry in db.ChangeTracker.Entries<AuditEntry>().Where(entry => entry.State == EntityState.Added).ToList())
            {
                entry.State = EntityState.Detached;
            }

            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Shelf.Audit").LogWarning(ex, "A sign-in could not be written to the activity log.");
        }
        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            ReaderRules.Principal(reader, CookieAuthenticationDefaults.AuthenticationScheme, session),
            new AuthenticationProperties { IsPersistent = true });
    }

    private static string Back(string page, AccountProblem problem, string? name, string? returnUrl, string? email = null, string? invite = null)
    {
        var query = new Dictionary<string, string?> { ["problem"] = problem.ToString() };
        if (!string.IsNullOrWhiteSpace(name))
        {
            query["name"] = ReaderRules.Clean(name) is { Length: <= ReaderRules.MaxNameLength } cleaned ? cleaned : null;
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            query["email"] = email.Trim() is { Length: <= EmailRules.MaxAddressLength } trimmed ? trimmed : null;
        }

        if (ReaderRules.IsLocalUrl(returnUrl))
        {
            query["returnUrl"] = returnUrl;
        }

        if (!string.IsNullOrWhiteSpace(invite) && invite.Length <= 100)
        {
            query["invite"] = invite;
        }

        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(page, query);
    }
}

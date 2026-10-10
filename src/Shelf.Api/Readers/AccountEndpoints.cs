using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
        account.MapPost("/two-factor/start", StartTwoFactor).RequireAuthorization();
        account.MapPost("/two-factor/confirm", ConfirmTwoFactor).RequireAuthorization();
        account.MapPost("/two-factor/disable", DisableTwoFactor).RequireAuthorization();
        account.MapPost("/signup", SignUp).AllowAnonymous().RequireRateLimiting(SignInLimit);
        account.MapPost("/signout", SignOut).AllowAnonymous();
        account.MapPost("/remove", RemoveSelf).RequireAuthorization();
        account.MapPost("/forgot", Forgot).AllowAnonymous().RequireRateLimiting(SignInLimit);
        account.MapPost("/reset", Reset).AllowAnonymous().RequireRateLimiting(SignInLimit);
        account.MapPut("/email", SetEmail).RequireAuthorization();
        account.MapPost("/email/test", TestEmail).RequireAuthorization();

        app.MapGet("/readers", Others).WithTags("Account").RequireAuthorization();
    }

    private static async Task<RedirectHttpResult> SignIn(
        [FromForm] string? name,
        [FromForm] string? password,
        [FromForm] string? returnUrl,
        HttpContext http,
        ShelfDb db,
        IDataProtectionProvider protection,
        CancellationToken cancellationToken)
    {
        var reader = await ReaderRules.VerifyAsync(db, name, password, cancellationToken);
        if (reader is null)
        {
            return TypedResults.Redirect(Back("/signin", AccountProblem.WrongPassword, name, returnUrl));
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

    private static async Task<RedirectHttpResult> SignInCode(
        [FromForm] string? code,
        [FromForm] string? returnUrl,
        HttpContext http,
        ShelfDb db,
        IDataProtectionProvider protection,
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

        if (!await TwoFactor.CheckAsync(db, protection, reader, code, cancellationToken))
        {
            var again = new Dictionary<string, string?> { ["problem"] = nameof(AccountProblem.CodeWrong) };
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
        await TwoFactor.EndSessionAsync(db, db.ReaderId, id, cancellationToken) > 0 ? TypedResults.NoContent() : TypedResults.NotFound();

    private static async Task<Ok<TwoFactorStart>> StartTwoFactor(ShelfDb db, IDataProtectionProvider protection, CancellationToken cancellationToken)
    {
        var reader = await db.Readers.FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        var secret = TwoFactor.NewSecret();
        reader.TwoFactorSecret = TwoFactor.Protector(protection).Protect(secret);
        reader.TwoFactorEnabled = false;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new TwoFactorStart(secret, TwoFactor.Uri(secret, reader.Name)));
    }

    private static async Task<Results<Ok<RecoveryCodes>, ValidationProblem>> ConfirmTwoFactor(
        TwoFactorCodeRequest request,
        ShelfDb db,
        IDataProtectionProvider protection,
        CancellationToken cancellationToken)
    {
        var reader = await db.Readers.FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        if (reader.TwoFactorSecret is null
            || !TwoFactor.Verify(TwoFactor.Protector(protection).Unprotect(reader.TwoFactorSecret), request.Code, DateTimeOffset.UtcNow))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Code"] = [ReaderRules.Describe(AccountProblem.CodeWrong)] });
        }

        reader.TwoFactorEnabled = true;
        await db.SaveChangesAsync(cancellationToken);
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
        [FromForm] string? password,
        [FromForm] string? confirm,
        [FromForm] string? returnUrl,
        HttpContext http,
        ShelfDb db,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!SignUpOpen(configuration) && await db.Readers.AnyAsync(cancellationToken))
        {
            return TypedResults.Redirect(Back("/signup", AccountProblem.SignUpClosed, name, returnUrl));
        }

        if (!string.Equals(password, confirm, StringComparison.Ordinal))
        {
            return TypedResults.Redirect(Back("/signup", AccountProblem.PasswordsDiffer, name, returnUrl));
        }

        var (reader, problem) = await ReaderRules.CreateAsync(db, name, password, cancellationToken);
        if (reader is null)
        {
            return TypedResults.Redirect(Back("/signup", problem ?? AccountProblem.NameMissing, name, returnUrl));
        }

        await SignInAsync(http, db, reader, cancellationToken);
        return TypedResults.Redirect(ReaderRules.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }

    private static async Task<Results<RedirectHttpResult, BadRequest>> SignOut(HttpContext http, IAntiforgery antiforgery, ShelfDb db)
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
        return TypedResults.Redirect("/signin");
    }

    private static async Task<Results<NoContent, ValidationProblem>> RemoveSelf(
        RemoveAccountRequest request,
        HttpContext http,
        ShelfDb db,
        EbookStore ebooks,
        AudioStore audio,
        CancellationToken cancellationToken)
    {
        var problem = await ReaderRules.RemoveSelfAsync(db, ebooks, audio, db.ReaderId, request.Password, cancellationToken);
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
        HttpContext http,
        ShelfDb db,
        IEmailSender email,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        await EmailRules.RequestResetAsync(db, email, who, EmailRules.PublicAddress(configuration, http.Request), cancellationToken);
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

    private static async Task<Results<NoContent, ValidationProblem>> SetEmail(EmailSettingsRequest request, ShelfDb db, CancellationToken cancellationToken)
    {
        var problem = await ReaderRules.SetEmailAsync(db, db.ReaderId, request.Email, request.Reminders, cancellationToken);
        return problem is null
            ? TypedResults.NoContent()
            : TypedResults.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Email)] = [ReaderRules.Describe(problem.Value)] });
    }

    private static async Task<Results<NoContent, ValidationProblem>> TestEmail(ShelfDb db, IEmailSender email, CancellationToken cancellationToken)
    {
        var reader = await db.Readers.AsNoTracking().FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        if (!email.Enabled || reader.Email is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Email"] = ["Add an address, and an admin has to set up a mail server, first."] });
        }

        await email.SendAsync(new EmailMessage(reader.Email, "Shelf can reach you", $"Hello {reader.Name},\n\nThis is a test from your shelf. Email works."), cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<ReaderResponse[]>> Others(ShelfDb db, ShelfReader reader, CancellationToken cancellationToken) =>
        TypedResults.Ok(await ReaderRules.OthersAsync(db, reader.Id ?? 0, cancellationToken));

    public static bool SignUpOpen(IConfiguration configuration) =>
        configuration.GetValue("Accounts:AllowSignUp", true);

    // Every sign-in is a session of its own, listed on the account page until it signs out.
    private static async Task SignInAsync(HttpContext http, ShelfDb db, Reader reader, CancellationToken cancellationToken)
    {
        var session = await TwoFactor.StartSessionAsync(db, reader.Id, TwoFactor.DeviceName(http.Request.Headers.UserAgent), cancellationToken);
        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            ReaderRules.Principal(reader, CookieAuthenticationDefaults.AuthenticationScheme, session),
            new AuthenticationProperties { IsPersistent = true });
    }

    private static string Back(string page, AccountProblem problem, string? name, string? returnUrl)
    {
        var query = new Dictionary<string, string?> { ["problem"] = problem.ToString() };
        if (!string.IsNullOrWhiteSpace(name))
        {
            query["name"] = ReaderRules.Clean(name) is { Length: <= ReaderRules.MaxNameLength } cleaned ? cleaned : null;
        }

        if (ReaderRules.IsLocalUrl(returnUrl))
        {
            query["returnUrl"] = returnUrl;
        }

        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(page, query);
    }
}

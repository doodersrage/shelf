using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Books;
using Shelf.Api.Data;

namespace Shelf.Api.Readers;

public static class AccountEndpoints
{
    public const string SignInLimit = "sign-in";

    public static void MapAccounts(this IEndpointRouteBuilder app)
    {
        var account = app.MapGroup("/account").WithTags("Account");
        account.MapPost("/signin", SignIn).AllowAnonymous().RequireRateLimiting(SignInLimit);
        account.MapPost("/signup", SignUp).AllowAnonymous().RequireRateLimiting(SignInLimit);
        account.MapPost("/signout", SignOut).AllowAnonymous();
        account.MapPost("/remove", RemoveSelf).RequireAuthorization();

        app.MapGet("/readers", Others).WithTags("Account").RequireAuthorization();
    }

    private static async Task<RedirectHttpResult> SignIn(
        [FromForm] string? name,
        [FromForm] string? password,
        [FromForm] string? returnUrl,
        HttpContext http,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        var reader = await ReaderRules.VerifyAsync(db, name, password, cancellationToken);
        if (reader is null)
        {
            return TypedResults.Redirect(Back("/signin", AccountProblem.WrongPassword, name, returnUrl));
        }

        await SignInAsync(http, reader);
        return TypedResults.Redirect(ReaderRules.IsLocalUrl(returnUrl) ? returnUrl! : "/");
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

        await SignInAsync(http, reader);
        return TypedResults.Redirect(ReaderRules.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }

    private static async Task<Results<RedirectHttpResult, BadRequest>> SignOut(HttpContext http, IAntiforgery antiforgery)
    {
        if (!await antiforgery.IsRequestValidAsync(http))
        {
            return TypedResults.BadRequest();
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

    private static async Task<Ok<ReaderResponse[]>> Others(ShelfDb db, ShelfReader reader, CancellationToken cancellationToken) =>
        TypedResults.Ok(await ReaderRules.OthersAsync(db, reader.Id ?? 0, cancellationToken));

    public static bool SignUpOpen(IConfiguration configuration) =>
        configuration.GetValue("Accounts:AllowSignUp", true);

    private static Task SignInAsync(HttpContext http, Reader reader) =>
        http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            ReaderRules.Principal(reader, CookieAuthenticationDefaults.AuthenticationScheme),
            new AuthenticationProperties { IsPersistent = true });

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

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Readers;

// A token a reader makes for a script or a home dashboard such as Home Assistant: "Authorization: Bearer shelf_…".
// It opens the books API only, never the account (passwords, keys, other tokens) or the admin pages, so a token that
// leaks cannot take the account over. A read-only token is refused anything but reading.
public sealed class ApiToken
{
    public const int MaxNameLength = 80;

    public int Id { get; set; }
    public int ReaderId { get; set; }
    public required string Name { get; set; }
    public required string Hash { get; set; }
    public bool CanChange { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

public sealed record ApiTokenSummary(int Id, string Name, bool CanChange, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

public sealed record NewApiTokenRequest(string? Name, bool CanChange);

public sealed record NewApiToken(int Id, string Name, bool CanChange, string Token);

public static class ApiTokens
{
    public const string SchemeName = "ApiToken";
    public const string Prefix = "shelf_";
    public const int MaxPerReader = 20;
    public const string ChangeClaim = "shelf:token-changes";

    // What a token opens: the library and its files, reading, lending, and the reader list to lend to.
    private static readonly string[] Opens = ["/books", "/settings", "/readers", "/version"];

    // A token's last use is kept to the minute, not written on every request.
    private static readonly TimeSpan UseGranularity = TimeSpan.FromMinutes(1);

    public static void Map(RouteGroupBuilder account)
    {
        account.MapGet("/tokens", List).RequireAuthorization();
        account.MapPost("/tokens", Create).RequireAuthorization();
        account.MapDelete("/tokens/{id:int}", Remove).RequireAuthorization();
    }

    public static async Task<ApiTokenSummary[]> ListAsync(ShelfDb db, int readerId, CancellationToken cancellationToken = default) =>
        (await db.ApiTokens.AsNoTracking().Where(token => token.ReaderId == readerId).ToListAsync(cancellationToken))
            .OrderBy(token => token.CreatedAt)
            .Select(token => new ApiTokenSummary(token.Id, token.Name, token.CanChange, token.CreatedAt, token.LastUsedAt))
            .ToArray();

    public static async Task<(NewApiToken? Made, string? Problem)> CreateAsync(ShelfDb db, int readerId, string? name, bool canChange, CancellationToken cancellationToken = default)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return (null, T("Name the token after what will use it."));
        }

        if (name.Length > ApiToken.MaxNameLength)
        {
            return (null, T("Keep the name to {0} characters.", ApiToken.MaxNameLength));
        }

        if (await db.ApiTokens.CountAsync(token => token.ReaderId == readerId, cancellationToken) >= MaxPerReader)
        {
            return (null, T("A reader can keep up to {0} tokens.", MaxPerReader));
        }

        var secret = Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = new ApiToken { ReaderId = readerId, Name = name, Hash = Hash(secret), CanChange = canChange, CreatedAt = DateTimeOffset.UtcNow };
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync(cancellationToken);
        await Audit.NoteAsync(db, Say("Made an API token"), await db.Readers.FindAsync([readerId], cancellationToken), name, cancellationToken);
        return (new NewApiToken(token.Id, token.Name, token.CanChange, secret), null);
    }

    public static async Task<bool> RemoveAsync(ShelfDb db, int readerId, int id, CancellationToken cancellationToken = default)
    {
        var token = await db.ApiTokens.FirstOrDefaultAsync(item => item.Id == id && item.ReaderId == readerId, cancellationToken);
        if (token is null)
        {
            return false;
        }

        db.ApiTokens.Remove(token);
        await db.SaveChangesAsync(cancellationToken);
        await Audit.NoteAsync(db, Say("Removed an API token"), await db.Readers.FindAsync([readerId], cancellationToken), token.Name, cancellationToken);
        return true;
    }

    // After authentication and anti-forgery: a token reaches only the books API, a read-only one only reads, and a
    // request carrying a token needs no anti-forgery token, as no browser sends one by itself.
    public static async Task Guard(HttpContext context, RequestDelegate next)
    {
        if (context.User.Identity is { IsAuthenticated: true, AuthenticationType: SchemeName })
        {
            var path = context.Request.Path;
            if (!Opens.Any(open => path.StartsWithSegments(open, StringComparison.OrdinalIgnoreCase)))
            {
                await Refuse(context, T("An API token opens the books API only, not account or admin pages."));
                return;
            }

            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !context.User.HasClaim(ChangeClaim, "true"))
            {
                await Refuse(context, T("This API token can only read. Make one that can change things to do this."));
                return;
            }

            context.Features.Set<IAntiforgeryValidationFeature>(new Checked());
        }

        await next(context);
    }

    internal static async Task<AuthenticateResult> AuthenticateAsync(ShelfDb db, string secret, CancellationToken cancellationToken)
    {
        if (secret.Length > 100)
        {
            return AuthenticateResult.Fail("That API token is not one of this shelf's.");
        }

        var hash = Hash(secret.Trim());
        var token = await db.ApiTokens.FirstOrDefaultAsync(item => item.Hash == hash, cancellationToken);
        var reader = token is null ? null : await db.Readers.AsNoTracking().FirstOrDefaultAsync(item => item.Id == token.ReaderId, cancellationToken);
        if (token is null || reader is null)
        {
            return AuthenticateResult.Fail("That API token is not one of this shelf's.");
        }

        var now = DateTimeOffset.UtcNow;
        if (token.LastUsedAt is not { } used || now - used >= UseGranularity)
        {
            token.LastUsedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }

        var principal = ReaderRules.Principal(reader, SchemeName);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(ChangeClaim, token.CanChange ? "true" : "false"));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    private static Task Refuse(HttpContext context, string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context);

    private static async Task<Ok<ApiTokenSummary[]>> List(ShelfDb db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await ListAsync(db, db.ReaderId, cancellationToken));

    private static async Task<IResult> Create(NewApiTokenRequest request, ShelfDb db, CancellationToken cancellationToken)
    {
        var (made, problem) = await CreateAsync(db, db.ReaderId, request.Name, request.CanChange, cancellationToken);
        return made is null
            ? TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Name"] = [problem!] })
            : TypedResults.Ok(made);
    }

    private static async Task<IResult> Remove(int id, ShelfDb db, CancellationToken cancellationToken) =>
        await RemoveAsync(db, db.ReaderId, id, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound();

    private static string Hash(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    private sealed class Checked : IAntiforgeryValidationFeature
    {
        public bool IsValid => true;

        public Exception? Error => null;
    }
}

// Reads "Authorization: Bearer shelf_…". Other bearer keys (a device key) are left to ShelfKeyHandler.
public sealed class ApiTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ShelfDb db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        return header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase) && header.AsSpan(bearer.Length).TrimStart().StartsWith(ApiTokens.Prefix, StringComparison.Ordinal)
            ? ApiTokens.AuthenticateAsync(db, header[bearer.Length..].Trim(), Context.RequestAborted)
            : Task.FromResult(AuthenticateResult.NoResult());
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}

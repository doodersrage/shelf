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

    // The parts of the API it opens, by name and separated by commas (see ApiTokens.Areas); null for all of them.
    public string? Areas { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

public sealed record ApiTokenSummary(int Id, string Name, bool CanChange, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, string[]? Areas = null);

// Areas null or empty opens every part of the API; otherwise only those named, from ApiTokens.Areas.
public sealed record NewApiTokenRequest(string? Name, bool CanChange, string[]? Areas = null);

public sealed record NewApiToken(int Id, string Name, bool CanChange, string Token, string[]? Areas = null);

public static class ApiTokens
{
    public const string SchemeName = "ApiToken";
    public const string Prefix = "shelf_";
    public const int MaxPerReader = 20;
    public const string ChangeClaim = "shelf:token-changes";
    public const string AreasClaim = "shelf:token-areas";

    // The parts of the API a token can be kept to. Library is everything not named by another: books, reading,
    // lending, collections, goals, and settings.
    public const string Library = "library";
    public const string Files = "files";
    public const string Journal = "journal";
    public const string Clubs = "clubs";
    public const string Events = "events";
    public static readonly IReadOnlyList<string> Areas = [Library, Files, Journal, Clubs, Events];

    // Which area a path is in. Files are a book's e-book, recording, cover, and pictures, whole-shelf backups and the
    // nightly ones, and the file check.
    public static string AreaOf(PathString path)
    {
        if (path.StartsWithSegments("/books/journal", StringComparison.OrdinalIgnoreCase))
        {
            return Journal;
        }

        if (path.StartsWithSegments("/books/clubs", StringComparison.OrdinalIgnoreCase))
        {
            return Clubs;
        }

        if (path.StartsWithSegments("/books/events", StringComparison.OrdinalIgnoreCase))
        {
            return Events;
        }

        var parts = (path.Value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts is ["books", _, "ebook" or "audio" or "cover" or "photos" or "notes.md", ..] || parts is ["books", "export" or "import" or "notes.zip" or "backups" or "files", ..])
        {
            return Files;
        }

        return Library;
    }

    private static string? CleanAreas(IEnumerable<string>? areas)
    {
        var kept = (areas ?? []).Select(area => area.Trim().ToLowerInvariant()).Where(Areas.Contains).Distinct().Order(StringComparer.Ordinal).ToList();
        return kept.Count == 0 || kept.Count == Areas.Count ? null : string.Join(',', kept);
    }

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
            .Select(token => new ApiTokenSummary(token.Id, token.Name, token.CanChange, token.CreatedAt, token.LastUsedAt, token.Areas?.Split(',')))
            .ToArray();

    public static async Task<(NewApiToken? Made, string? Problem)> CreateAsync(ShelfDb db, int readerId, string? name, bool canChange, CancellationToken cancellationToken = default, IEnumerable<string>? areas = null)
    {
        name = name?.Trim();
        if (await Family.IsRestrictedAsync(db, readerId, cancellationToken))
        {
            return (null, T("A child's account cannot do that. Ask the grown-up who made it."));
        }

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
        var token = new ApiToken { ReaderId = readerId, Name = name, Hash = Hash(secret), CanChange = canChange, Areas = CleanAreas(areas), CreatedAt = DateTimeOffset.UtcNow };
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync(cancellationToken);
        await Audit.NoteAsync(db, Say("Made an API token"), await db.Readers.FindAsync([readerId], cancellationToken), name, cancellationToken);
        return (new NewApiToken(token.Id, token.Name, token.CanChange, secret, token.Areas?.Split(',')), null);
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

            if (context.User.FindFirst(AreasClaim)?.Value is { Length: > 0 } areas && !path.StartsWithSegments("/version", StringComparison.OrdinalIgnoreCase) && !areas.Split(',').Contains(AreaOf(path)))
            {
                await Refuse(context, T("This API token doesn't open that part of the shelf. Make one that does."));
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
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(AreasClaim, token.Areas ?? ""));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    // The reader a token belongs to, for an app that signs in with one in place of a password.
    public static async Task<Reader?> ReaderOfAsync(ShelfDb db, string secret, CancellationToken cancellationToken = default)
    {
        var hash = Hash(secret.Trim());
        var readerId = await db.ApiTokens.AsNoTracking().Where(item => item.Hash == hash).Select(item => (int?)item.ReaderId).FirstOrDefaultAsync(cancellationToken);
        return readerId is int id ? await db.Readers.AsNoTracking().FirstOrDefaultAsync(reader => reader.Id == id, cancellationToken) : null;
    }

    private static Task Refuse(HttpContext context, string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context);

    private static async Task<Ok<ApiTokenSummary[]>> List(ShelfDb db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await ListAsync(db, db.ReaderId, cancellationToken));

    private static async Task<IResult> Create(NewApiTokenRequest request, ShelfDb db, CancellationToken cancellationToken)
    {
        var (made, problem) = await CreateAsync(db, db.ReaderId, request.Name, request.CanChange, cancellationToken, request.Areas);
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

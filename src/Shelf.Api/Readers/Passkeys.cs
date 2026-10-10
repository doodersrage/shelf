using System.Security.Cryptography;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Readers;

// A passkey: a key pair kept by the reader's device or password manager. Shelf keeps only the public half.
public sealed class ReaderPasskey
{
    public int Id { get; set; }
    public int ReaderId { get; set; }
    public required byte[] CredentialId { get; set; }
    public required byte[] PublicKey { get; set; }
    public long SignCount { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

public sealed record PasskeySummary(int Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

public sealed record NewPasskey(string? Name, AuthenticatorAttestationRawResponse Credential);

public sealed record PasskeySignIn(AuthenticatorAssertionRawResponse Credential, string? ReturnUrl);

// Sign-in with a passkey (WebAuthn), through the Fido2 library. A passkey that checks the reader with a fingerprint,
// face, or PIN is two steps in one, so it goes in without the authenticator code.
public static class Passkeys
{
    public const int MaxPerReader = 20;
    private const string CreateCookie = "shelf-passkey-new";
    private const string SignInCookie = "shelf-passkey";
    private static readonly TimeSpan CeremonyLasts = TimeSpan.FromMinutes(5);

    public static void Map(RouteGroupBuilder account, string signInLimit)
    {
        account.MapGet("/passkeys", List).RequireAuthorization();
        account.MapPost("/passkeys/options", CreateOptions).RequireAuthorization().DisableAntiforgery();
        account.MapPost("/passkeys", Create).RequireAuthorization().DisableAntiforgery();
        account.MapDelete("/passkeys/{id:int}", Remove).RequireAuthorization();
        account.MapPost("/passkey/options", SignInOptions).AllowAnonymous().DisableAntiforgery().RequireRateLimiting(signInLimit);
        account.MapPost("/passkey/signin", SignIn).AllowAnonymous().DisableAntiforgery().RequireRateLimiting(signInLimit);
    }

    // The relying party is the shelf's own host name, and only its own address may use the passkeys.
    private static Fido2 Fido(HttpContext http, IConfiguration configuration)
    {
        var origin = configuration["Passkeys:Origin"] ?? $"{http.Request.Scheme}://{http.Request.Host}";
        return new Fido2(new Fido2Configuration
        {
            RPID = configuration["Passkeys:RelyingParty"] ?? new Uri(origin).Host,
            RPName = "Shelf",
            Origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { origin.TrimEnd('/') },
            TimestampDriftTolerance = 300_000,
        });
    }

    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider protection) =>
        protection.CreateProtector("Shelf.Passkeys.Ceremony").ToTimeLimitedDataProtector();

    private static void Remember(HttpContext http, IDataProtectionProvider protection, string cookie, string json) =>
        http.Response.Cookies.Append(cookie, Protector(protection).Protect(json, CeremonyLasts), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = http.Request.IsHttps,
            MaxAge = CeremonyLasts,
            Path = "/account",
        });

    private static string? Recall(HttpContext http, IDataProtectionProvider protection, string cookie)
    {
        var value = http.Request.Cookies[cookie];
        http.Response.Cookies.Delete(cookie, new CookieOptions { Path = "/account" });
        try
        {
            return value is null ? null : Protector(protection).Unprotect(value);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public static async Task<PasskeySummary[]> ListAsync(ShelfDb db, int readerId, CancellationToken cancellationToken = default) =>
        (await db.ReaderPasskeys.AsNoTracking().Where(key => key.ReaderId == readerId).ToListAsync(cancellationToken))
            .OrderBy(key => key.CreatedAt)
            .Select(key => new PasskeySummary(key.Id, key.Name, key.CreatedAt, key.LastUsedAt))
            .ToArray();

    private static async Task<Ok<PasskeySummary[]>> List(ShelfDb db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await ListAsync(db, db.ReaderId, cancellationToken));

    private static async Task<IResult> CreateOptions(HttpContext http, ShelfDb db, IConfiguration configuration, IDataProtectionProvider protection, CancellationToken cancellationToken)
    {
        var reader = await db.Readers.FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        var existing = await db.ReaderPasskeys.AsNoTracking().Where(key => key.ReaderId == reader.Id).Select(key => key.CredentialId).ToListAsync(cancellationToken);
        if (existing.Count >= MaxPerReader)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Passkey"] = [$"A reader can keep up to {MaxPerReader} passkeys."] });
        }

        // A random handle names the reader to the device; it says nothing about who they are.
        if (reader.PasskeyHandle is null)
        {
            reader.PasskeyHandle = RandomNumberGenerator.GetBytes(32);
            await db.SaveChangesAsync(cancellationToken);
        }

        var options = Fido(http, configuration).RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = reader.PasskeyHandle, Name = reader.Name, DisplayName = reader.Name },
            ExcludeCredentials = existing.Select(id => new PublicKeyCredentialDescriptor(id)).ToList(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Required,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });
        Remember(http, protection, CreateCookie, options.ToJson());
        return Results.Content(options.ToJson(), "application/json");
    }

    private static async Task<IResult> Create(
        NewPasskey request, HttpContext http, ShelfDb db, IConfiguration configuration, IDataProtectionProvider protection, CancellationToken cancellationToken)
    {
        if (Recall(http, protection, CreateCookie) is not { } json)
        {
            return Problem("That took too long. Try again.");
        }

        RegisteredPublicKeyCredential credential;
        try
        {
            credential = await Fido(http, configuration).MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = request.Credential,
                OriginalOptions = CredentialCreateOptions.FromJson(json),
                IsCredentialIdUniqueToUserCallback = async (args, token) => !await db.ReaderPasskeys.AnyAsync(key => key.CredentialId == args.CredentialId, token),
            }, cancellationToken);
        }
        catch (Fido2VerificationException)
        {
            return Problem("The passkey could not be checked. Try again.");
        }

        var reader = await db.Readers.FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        if (reader.PasskeyHandle is null || !credential.User.Id.AsSpan().SequenceEqual(reader.PasskeyHandle))
        {
            return Problem("That passkey belongs to someone else.");
        }

        var name = string.IsNullOrWhiteSpace(request.Name) ? TwoFactor.DeviceName(http.Request.Headers.UserAgent) : request.Name.Trim();
        db.ReaderPasskeys.Add(new ReaderPasskey
        {
            ReaderId = reader.Id,
            CredentialId = credential.Id,
            PublicKey = credential.PublicKey,
            SignCount = credential.SignCount,
            Name = name.Length > 80 ? name[..80] : name,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
        await Audit.NoteAsync(db, Localization.Words.Say("Added a passkey"), reader, name, cancellationToken);
        return TypedResults.Ok(await ListAsync(db, reader.Id, cancellationToken));
    }

    public static async Task<bool> RemoveAsync(ShelfDb db, int readerId, int id, CancellationToken cancellationToken = default)
    {
        var key = await db.ReaderPasskeys.FirstOrDefaultAsync(item => item.Id == id && item.ReaderId == readerId, cancellationToken);
        if (key is null)
        {
            return false;
        }

        db.ReaderPasskeys.Remove(key);
        await db.SaveChangesAsync(cancellationToken);
        await Audit.NoteAsync(db, Localization.Words.Say("Removed a passkey"), await db.Readers.FindAsync([readerId], cancellationToken), key.Name, cancellationToken);
        return true;
    }

    private static async Task<Results<NoContent, NotFound>> Remove(int id, ShelfDb db, CancellationToken cancellationToken) =>
        await RemoveAsync(db, db.ReaderId, id, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound();

    // Any passkey for this shelf will do; the device offers the ones it has.
    private static IResult SignInOptions(HttpContext http, IConfiguration configuration, IDataProtectionProvider protection)
    {
        var options = Fido(http, configuration).GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Required,
        });
        Remember(http, protection, SignInCookie, options.ToJson());
        return Results.Content(options.ToJson(), "application/json");
    }

    private static async Task<IResult> SignIn(
        PasskeySignIn request, HttpContext http, ShelfDb db, IConfiguration configuration, IDataProtectionProvider protection, CancellationToken cancellationToken)
    {
        if (Recall(http, protection, SignInCookie) is not { } json)
        {
            return Problem("That took too long. Try again.");
        }

        var key = await db.ReaderPasskeys.FirstOrDefaultAsync(item => item.CredentialId == request.Credential.RawId, cancellationToken);
        var reader = key is null ? null : await db.Readers.FirstOrDefaultAsync(item => item.Id == key.ReaderId, cancellationToken);
        if (key is null || reader?.PasskeyHandle is null)
        {
            return Problem("This shelf does not know that passkey. Sign in with the password, then add the passkey on Account.");
        }

        try
        {
            var result = await Fido(http, configuration).MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = request.Credential,
                OriginalOptions = AssertionOptions.FromJson(json),
                StoredPublicKey = key.PublicKey,
                StoredSignatureCounter = (uint)key.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                    Task.FromResult(args.UserHandle.AsSpan().SequenceEqual(reader.PasskeyHandle) && args.CredentialId.AsSpan().SequenceEqual(key.CredentialId)),
            }, cancellationToken);
            key.SignCount = result.SignCount;
        }
        catch (Fido2VerificationException)
        {
            await Audit.NoteAsync(db, Localization.Words.Say("A sign-in failed: the passkey did not check out"), reader, key.Name, cancellationToken);
            return Problem("The passkey could not be checked. Try again.");
        }

        key.LastUsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await AccountEndpoints.SignInAsync(http, db, reader, cancellationToken);
        return TypedResults.Ok(new { redirect = ReaderRules.IsLocalUrl(request.ReturnUrl) ? request.ReturnUrl : "/" });
    }

    private static IResult Problem(string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Passkey"] = [message] });
}

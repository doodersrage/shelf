using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Books;
using Shelf.Api.Data;

namespace Shelf.Api.Readers;

public static class ReaderRules
{
    public const int MaxNameLength = 40;
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 200;

    private static readonly PasswordHasher<Reader> Hasher = new();

    public static string Normalize(string name) => Clean(name).ToUpperInvariant();

    public static string Clean(string? name) =>
        string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static AccountProblem? NameProblem(string? name)
    {
        var cleaned = Clean(name);
        if (cleaned.Length == 0)
        {
            return AccountProblem.NameMissing;
        }

        if (cleaned.Length > MaxNameLength)
        {
            return AccountProblem.NameTooLong;
        }

        return cleaned.All(character => char.IsLetterOrDigit(character) || character is ' ' or '.' or '-' or '_' or '\'')
            ? null
            : AccountProblem.NameCharacters;
    }

    public static AccountProblem? PasswordProblem(string? password) => password switch
    {
        null or { Length: < MinPasswordLength } => AccountProblem.PasswordTooShort,
        { Length: > MaxPasswordLength } => AccountProblem.PasswordTooLong,
        _ => null,
    };

    public static string Describe(AccountProblem problem) => problem switch
    {
        AccountProblem.NameMissing => "Choose a name.",
        AccountProblem.NameTooLong => $"Keep the name to {MaxNameLength} characters.",
        AccountProblem.NameCharacters => "Use letters, numbers, spaces, and . - _ ' in a name.",
        AccountProblem.NameTaken => "That name is taken.",
        AccountProblem.PasswordTooShort => $"Use a password of at least {MinPasswordLength} characters.",
        AccountProblem.PasswordTooLong => $"Keep the password to {MaxPasswordLength} characters.",
        AccountProblem.PasswordsDiffer => "The two passwords are different.",
        AccountProblem.WrongPassword => "That name and password do not match.",
        AccountProblem.CurrentPasswordWrong => "The current password does not match.",
        AccountProblem.SignUpClosed => "This shelf is not taking new readers.",
        _ => "Something went wrong.",
    };

    public static async Task<(Reader? Reader, AccountProblem? Problem)> CreateAsync(
        ShelfDb db,
        string? name,
        string? password,
        CancellationToken cancellationToken = default)
    {
        var problem = NameProblem(name) ?? PasswordProblem(password);
        if (problem is not null)
        {
            return (null, problem);
        }

        var normalized = Normalize(name!);
        if (await db.Readers.AnyAsync(reader => reader.NormalizedName == normalized, cancellationToken))
        {
            return (null, AccountProblem.NameTaken);
        }

        var reader = new Reader
        {
            Name = Clean(name),
            NormalizedName = normalized,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        reader.PasswordHash = Hasher.HashPassword(reader, password!);
        db.Readers.Add(reader);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return (null, AccountProblem.NameTaken);
        }

        if (!await db.Readers.AnyAsync(other => other.Id != reader.Id, cancellationToken))
        {
            await ClaimUnownedAsync(db, reader.Id, cancellationToken);
        }

        return (reader, null);
    }

    public static async Task<Reader?> VerifyAsync(
        ShelfDb db,
        string? name,
        string? password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(password) || password.Length > MaxPasswordLength)
        {
            return null;
        }

        var normalized = Normalize(name);
        var reader = await db.Readers.FirstOrDefaultAsync(item => item.NormalizedName == normalized, cancellationToken);
        if (reader is null)
        {
            return null;
        }

        var result = Hasher.VerifyHashedPassword(reader, reader.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            return null;
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            reader.PasswordHash = Hasher.HashPassword(reader, password);
            await db.SaveChangesAsync(cancellationToken);
        }

        return reader;
    }

    public static async Task<AccountProblem?> ChangePasswordAsync(
        ShelfDb db,
        int readerId,
        string? current,
        string? password,
        CancellationToken cancellationToken = default)
    {
        var reader = await db.Readers.FirstOrDefaultAsync(item => item.Id == readerId, cancellationToken);
        if (reader is null || string.IsNullOrEmpty(current)
            || Hasher.VerifyHashedPassword(reader, reader.PasswordHash, current) == PasswordVerificationResult.Failed)
        {
            return AccountProblem.CurrentPasswordWrong;
        }

        if (PasswordProblem(password) is { } problem)
        {
            return problem;
        }

        reader.PasswordHash = Hasher.HashPassword(reader, password!);
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    // The books and settings from before there were accounts go to the first reader.
    public static async Task ClaimUnownedAsync(ShelfDb db, int readerId, CancellationToken cancellationToken = default)
    {
        await db.Books.IgnoreQueryFilters()
            .Where(book => book.OwnerId == null)
            .ExecuteUpdateAsync(set => set.SetProperty(book => book.OwnerId, readerId), cancellationToken);

        const int legacySettingId = 1;
        if (readerId == legacySettingId)
        {
            return;
        }

        var legacy = await db.Settings.FirstOrDefaultAsync(setting => setting.Id == legacySettingId, cancellationToken);
        if (legacy is null || await db.Readers.AnyAsync(reader => reader.Id == legacySettingId, cancellationToken))
        {
            return;
        }

        db.Settings.Remove(legacy);
        db.Settings.Add(new ShelfSetting
        {
            Id = readerId,
            YearlyGoal = legacy.YearlyGoal,
            SyncAddress = legacy.SyncAddress,
            SyncKey = legacy.SyncKey,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task<string> NewKeyAsync(ShelfDb db, int readerId, CancellationToken cancellationToken = default)
    {
        var reader = await db.Readers.FirstAsync(item => item.Id == readerId, cancellationToken);
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        reader.KeyHash = HashKey(key);
        await db.SaveChangesAsync(cancellationToken);
        return key;
    }

    public static async Task<bool> HasKeyAsync(ShelfDb db, int readerId, CancellationToken cancellationToken = default) =>
        await db.Readers.AnyAsync(reader => reader.Id == readerId && reader.KeyHash != null, cancellationToken);

    public static async Task<Reader?> FindByKeyAsync(ShelfDb db, string? key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100)
        {
            return null;
        }

        var hash = HashKey(key.Trim());
        return await db.Readers.AsNoTracking().FirstOrDefaultAsync(reader => reader.KeyHash == hash, cancellationToken);
    }

    public static async Task<ReaderResponse[]> OthersAsync(ShelfDb db, int readerId, CancellationToken cancellationToken = default)
    {
        var readers = await db.Readers.AsNoTracking()
            .Where(reader => reader.Id != readerId)
            .Select(reader => new ReaderResponse(reader.Id, reader.Name))
            .ToListAsync(cancellationToken);
        return readers.OrderBy(reader => reader.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static ClaimsPrincipal Principal(Reader reader, string scheme) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, reader.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, reader.Name),
            ],
            scheme));

    public static bool IsLocalUrl(string? url) =>
        !string.IsNullOrEmpty(url)
        && url[0] == '/'
        && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
        && !url.Contains('\\', StringComparison.Ordinal)
        && !url.Any(char.IsControl);

    private static string HashKey(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
}

public enum AccountProblem
{
    NameMissing,
    NameTooLong,
    NameCharacters,
    NameTaken,
    PasswordTooShort,
    PasswordTooLong,
    PasswordsDiffer,
    WrongPassword,
    CurrentPasswordWrong,
    SignUpClosed,
}

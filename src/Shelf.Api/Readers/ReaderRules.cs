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

    public const string StampClaim = "shelf:stamp";

    private static readonly PasswordHasher<Reader> Hasher = new();

    public static string NewStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    // Readable enough to read aloud or copy: four groups of four from an alphabet without look-alikes.
    public static string NewPassword()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        var groups = Enumerable.Range(0, 4)
            .Select(_ => new string(Enumerable.Range(0, 4).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray()));
        return string.Join('-', groups);
    }

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
        AccountProblem.LastAdmin => "The shelf needs at least one admin. Make another reader an admin first.",
        AccountProblem.NoSuchReader => "That reader is not on this shelf.",
        AccountProblem.ResetExpired => "That reset link has expired or was used already. Ask for a new one.",
        AccountProblem.EmailInvalid => "That does not look like an email address.",
        AccountProblem.CodeWrong => "That code does not match. Try the newest code from your authenticator, or a recovery code.",
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
            reader.IsAdmin = true;
            await db.SaveChangesAsync(cancellationToken);
            await ClaimUnownedAsync(db, reader.Id, cancellationToken);
        }

        await Audit.NoteAsync(db, reader.IsAdmin ? "Signed up, the first reader and admin" : "Signed up", reader, cancellationToken: cancellationToken);
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
            await Audit.NoteAsync(db, "A sign-in failed: wrong password", reader, cancellationToken: cancellationToken);
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
        reader.Stamp = NewStamp();
        await db.SaveChangesAsync(cancellationToken);
        await db.ReaderSessions.Where(session => session.ReaderId == readerId).ExecuteDeleteAsync(cancellationToken);
        await Audit.NoteAsync(db, "Changed the password", reader, cancellationToken: cancellationToken);
        return null;
    }

    // A new password from a reset link: every session from before it ends.
    public static async Task<bool> SetPasswordAsync(ShelfDb db, int readerId, string password, CancellationToken cancellationToken = default)
    {
        var reader = await db.Readers.FirstOrDefaultAsync(item => item.Id == readerId, cancellationToken);
        if (reader is null)
        {
            return false;
        }

        reader.PasswordHash = Hasher.HashPassword(reader, password);
        reader.Stamp = NewStamp();
        await db.SaveChangesAsync(cancellationToken);
        await db.ReaderSessions.Where(session => session.ReaderId == readerId).ExecuteDeleteAsync(cancellationToken);
        await Audit.NoteAsync(db, "Set a new password", reader, cancellationToken: cancellationToken);
        return true;
    }

    public static async Task<AccountProblem?> SetEmailAsync(ShelfDb db, int readerId, string? email, bool reminders, CancellationToken cancellationToken = default)
    {
        var address = EmailRules.CleanAddress(email);
        if (address == "")
        {
            return AccountProblem.EmailInvalid;
        }

        var reader = await db.Readers.FirstOrDefaultAsync(item => item.Id == readerId, cancellationToken);
        if (reader is null)
        {
            return AccountProblem.NoSuchReader;
        }

        reader.Email = address;
        reader.EmailReminders = address is not null && reminders;
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    // For a reader who has forgotten their password: a new one to hand them, and every old session ends.
    public static async Task<string?> ResetPasswordAsync(ShelfDb db, int readerId, CancellationToken cancellationToken = default)
    {
        var reader = await db.Readers.FirstOrDefaultAsync(item => item.Id == readerId, cancellationToken);
        if (reader is null)
        {
            return null;
        }

        var password = NewPassword();
        reader.PasswordHash = Hasher.HashPassword(reader, password);
        reader.Stamp = NewStamp();
        await db.SaveChangesAsync(cancellationToken);
        await db.ReaderSessions.Where(session => session.ReaderId == readerId).ExecuteDeleteAsync(cancellationToken);

        await Audit.NoteAsync(db, "Gave a new password", reader, cancellationToken: cancellationToken);

        // An admin's reset is the way back in after a lost phone, so it turns two-step sign-in off too.
        await TwoFactor.DisableAsync(db, reader, cancellationToken);
        return password;
    }

    public static async Task<bool> IsAdminAsync(ShelfDb db, int readerId, CancellationToken cancellationToken = default) =>
        readerId != 0 && await db.Readers.AnyAsync(reader => reader.Id == readerId && reader.IsAdmin, cancellationToken);

    public static async Task<bool> StampMatchesAsync(ShelfDb db, int readerId, string? stamp, CancellationToken cancellationToken = default) =>
        !string.IsNullOrEmpty(stamp)
        && await db.Readers.AnyAsync(reader => reader.Id == readerId && reader.Stamp == stamp, cancellationToken);

    public static async Task<ReaderSummary[]> SummariesAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var counts = await db.Books.IgnoreQueryFilters()
            .Where(book => book.OwnerId != null)
            .GroupBy(book => book.OwnerId!.Value)
            .Select(group => new { Id = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Id, item => item.Count, cancellationToken);
        var readers = await db.Readers.AsNoTracking().ToListAsync(cancellationToken);
        return readers
            .OrderBy(reader => reader.Name, StringComparer.OrdinalIgnoreCase)
            .Select(reader => new ReaderSummary(reader.Id, reader.Name, reader.IsAdmin, reader.CreatedAt, counts.GetValueOrDefault(reader.Id)))
            .ToArray();
    }

    public static async Task<AccountProblem?> SetAdminAsync(ShelfDb db, int readerId, bool isAdmin, CancellationToken cancellationToken = default)
    {
        var reader = await db.Readers.FirstOrDefaultAsync(item => item.Id == readerId, cancellationToken);
        if (reader is null)
        {
            return AccountProblem.NoSuchReader;
        }

        if (!isAdmin && reader.IsAdmin && !await db.Readers.AnyAsync(other => other.IsAdmin && other.Id != readerId, cancellationToken))
        {
            return AccountProblem.LastAdmin;
        }

        var changed = reader.IsAdmin != isAdmin;
        reader.IsAdmin = isAdmin;
        await db.SaveChangesAsync(cancellationToken);
        if (changed)
        {
            await Audit.NoteAsync(db, isAdmin ? "Made an admin" : "Took away admin rights", reader, cancellationToken: cancellationToken);
        }

        return null;
    }

    public static async Task<AccountProblem?> RemoveSelfAsync(
        ShelfDb db,
        EbookStore ebooks,
        AudioStore audio,
        int readerId,
        string? password,
        CancellationToken cancellationToken = default)
    {
        var reader = await db.Readers.AsNoTracking().FirstOrDefaultAsync(item => item.Id == readerId, cancellationToken);
        if (reader is null || string.IsNullOrEmpty(password)
            || Hasher.VerifyHashedPassword(reader, reader.PasswordHash, password) == PasswordVerificationResult.Failed)
        {
            return AccountProblem.CurrentPasswordWrong;
        }

        return await RemoveAsync(db, ebooks, audio, readerId, cancellationToken);
    }

    // Removing a reader takes their shelf with them: their books and files go, and books lent to them go home.
    public static async Task<AccountProblem?> RemoveAsync(
        ShelfDb db,
        EbookStore ebooks,
        AudioStore audio,
        int readerId,
        CancellationToken cancellationToken = default)
    {
        var reader = await db.Readers.FirstOrDefaultAsync(item => item.Id == readerId, cancellationToken);
        if (reader is null)
        {
            return AccountProblem.NoSuchReader;
        }

        if (reader.IsAdmin && !await db.Readers.AnyAsync(other => other.IsAdmin && other.Id != readerId, cancellationToken))
        {
            return AccountProblem.LastAdmin;
        }

        var files = new List<(string? Ebook, string? Audio)>();
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var borrowed = await db.Books.IgnoreQueryFilters().Where(book => book.BorrowerId == readerId).ToListAsync(cancellationToken);
            foreach (var book in borrowed)
            {
                BookRules.ReturnLoan(book);
            }

            var owned = await db.Books.IgnoreQueryFilters().Where(book => book.OwnerId == readerId).ToListAsync(cancellationToken);
            files.AddRange(owned.Select(book => (book.EbookStoredName, book.AudioStoredName)));
            db.Books.RemoveRange(owned);
            db.Settings.RemoveRange(await db.Settings.Where(setting => setting.Id == readerId).ToListAsync(cancellationToken));
            db.Readers.Remove(reader);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        foreach (var (ebook, recording) in files)
        {
            ebooks.Delete(ebook);
            audio.Delete(recording);
        }

        await BookRules.RemoveUnusedTagsAsync(db, cancellationToken);
        await Audit.NoteAsync(db, "Removed the account and its shelf", reader, $"{files.Count} books", cancellationToken);
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
        await Audit.NoteAsync(db, "Made a new device key", reader, cancellationToken: cancellationToken);
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

    public static ClaimsPrincipal Principal(Reader reader, string scheme, string? sessionId = null)
    {
        List<Claim> claims =
        [
            new Claim(ClaimTypes.NameIdentifier, reader.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, reader.Name),
            new Claim(StampClaim, reader.Stamp),
        ];
        if (sessionId is not null)
        {
            claims.Add(new Claim(TwoFactor.SessionClaim, sessionId));
        }

        return new(new ClaimsIdentity(claims, scheme));
    }

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
    LastAdmin,
    NoSuchReader,
    ResetExpired,
    EmailInvalid,
    CodeWrong,
}

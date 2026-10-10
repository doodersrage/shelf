using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Readers;

// One signed-in browser or device. Signing out, here or from another device, removes it.
public sealed class ReaderSession
{
    public required string Id { get; set; }
    public int ReaderId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public string? Device { get; set; }
}

// A single-use way in when the authenticator is lost. Only the hash is kept.
public sealed class RecoveryCode
{
    public int Id { get; set; }
    public int ReaderId { get; set; }
    public required string CodeHash { get; set; }
    public bool Used { get; set; }
}

public sealed record SessionSummary(string Id, string? Device, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, bool Current);

// Time-based one-time codes (RFC 6238) from an authenticator app, as a second step after the password.
public static class TwoFactor
{
    public const string Issuer = "Shelf";
    public const string SessionClaim = "shelf:session";
    public const int RecoveryCodeCount = 10;
    private const int Step = 30;

    public static string NewSecret() => Base32(RandomNumberGenerator.GetBytes(20));

    public static string Uri(string secret, string readerName) =>
        $"otpauth://totp/{System.Uri.EscapeDataString($"{Issuer}:{readerName}")}?secret={secret}&issuer={Issuer}&digits=6&period={Step}";

    public static string QrSvg(string uri)
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(uri, QRCoder.QRCodeGenerator.ECCLevel.M);
        return new QRCoder.SvgQRCode(data).GetGraphic(6, "#252a28", "#ffffff", drawQuietZones: true);
    }

    public static string Code(string secret, long counter)
    {
        Span<byte> message = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(message, counter);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(FromBase32(secret), message, hash);
        var offset = hash[19] & 0x0f;
        var value = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    // The code from the current 30 seconds, or the ones either side, for a phone clock a little off.
    public static bool Verify(string secret, string? code, DateTimeOffset now)
    {
        var digits = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length != 6)
        {
            return false;
        }

        var counter = now.ToUnixTimeSeconds() / Step;
        for (var drift = -1; drift <= 1; drift++)
        {
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, counter + drift)), Encoding.ASCII.GetBytes(digits)))
            {
                return true;
            }
        }

        return false;
    }

    public static IDataProtector Protector(IDataProtectionProvider provider) => provider.CreateProtector("Shelf.TwoFactor.Secret");

    public static async Task<string[]> NewRecoveryCodesAsync(ShelfDb db, int readerId, CancellationToken cancellationToken = default)
    {
        await db.RecoveryCodes.Where(code => code.ReaderId == readerId).ExecuteDeleteAsync(cancellationToken);
        var codes = Enumerable.Range(0, RecoveryCodeCount).Select(_ => ReaderRules.NewPassword()[..9]).ToArray();
        db.RecoveryCodes.AddRange(codes.Select(code => new RecoveryCode { ReaderId = readerId, CodeHash = Hash(code) }));
        await db.SaveChangesAsync(cancellationToken);
        return codes;
    }

    public static async Task<bool> UseRecoveryCodeAsync(ShelfDb db, int readerId, string? code, CancellationToken cancellationToken = default)
    {
        var cleaned = (code ?? "").Trim().ToLowerInvariant();
        if (cleaned.Length != 9)
        {
            return false;
        }

        var hash = Hash(cleaned);
        var match = await db.RecoveryCodes.FirstOrDefaultAsync(item => item.ReaderId == readerId && item.CodeHash == hash && !item.Used, cancellationToken);
        if (match is null)
        {
            return false;
        }

        match.Used = true;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // The second step: a code from the authenticator, or one of the recovery codes.
    public static async Task<bool> CheckAsync(ShelfDb db, IDataProtectionProvider protection, Reader reader, string? code, CancellationToken cancellationToken = default)
    {
        if (!reader.TwoFactorEnabled || reader.TwoFactorSecret is null)
        {
            return true;
        }

        string secret;
        try
        {
            secret = Protector(protection).Unprotect(reader.TwoFactorSecret);
        }
        catch (CryptographicException)
        {
            return false;
        }

        return Verify(secret, code, DateTimeOffset.UtcNow) || await UseRecoveryCodeAsync(db, reader.Id, code, cancellationToken);
    }

    public static async Task DisableAsync(ShelfDb db, Reader reader, CancellationToken cancellationToken = default)
    {
        if (reader.TwoFactorEnabled)
        {
            await Audit.NoteAsync(db, "Turned off two-step sign-in", reader, cancellationToken: cancellationToken);
        }

        reader.TwoFactorEnabled = false;
        reader.TwoFactorSecret = null;
        await db.RecoveryCodes.Where(code => code.ReaderId == reader.Id).ExecuteDeleteAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task<string> StartSessionAsync(ShelfDb db, int readerId, string? device, CancellationToken cancellationToken = default)
    {
        var session = new ReaderSession
        {
            Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            ReaderId = readerId,
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            Device = device is { Length: > 200 } ? device[..200] : device,
        };
        db.ReaderSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return session.Id;
    }

    // Still signed in on this device? Notes when it was last seen, at most every few minutes.
    public static async Task<bool> TouchSessionAsync(ShelfDb db, int readerId, string? sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return false;
        }

        var session = await db.ReaderSessions.FirstOrDefaultAsync(item => item.Id == sessionId && item.ReaderId == readerId, cancellationToken);
        if (session is null)
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - session.LastSeenAt > TimeSpan.FromMinutes(5))
        {
            session.LastSeenAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    public static async Task<SessionSummary[]> SessionsAsync(ShelfDb db, int readerId, string? current, CancellationToken cancellationToken = default)
    {
        var sessions = await db.ReaderSessions.AsNoTracking().Where(item => item.ReaderId == readerId).ToListAsync(cancellationToken);
        return sessions
            .OrderByDescending(item => item.Id == current)
            .ThenByDescending(item => item.LastSeenAt)
            .Select(item => new SessionSummary(item.Id, item.Device, item.CreatedAt, item.LastSeenAt, item.Id == current))
            .ToArray();
    }

    public static Task<int> EndSessionAsync(ShelfDb db, int readerId, string sessionId, CancellationToken cancellationToken = default) =>
        db.ReaderSessions.Where(item => item.ReaderId == readerId && item.Id == sessionId).ExecuteDeleteAsync(cancellationToken);

    // Signing another device out from the list, which the log keeps; signing out of this browser is everyday.
    public static async Task<int> SignOutDeviceAsync(ShelfDb db, int readerId, string sessionId, CancellationToken cancellationToken = default)
    {
        var device = await db.ReaderSessions.Where(item => item.ReaderId == readerId && item.Id == sessionId).Select(item => item.Device).FirstOrDefaultAsync(cancellationToken);
        var ended = await EndSessionAsync(db, readerId, sessionId, cancellationToken);
        if (ended > 0)
        {
            await Audit.NoteAsync(db, "Signed out a device", await db.Readers.FindAsync([readerId], cancellationToken), device, cancellationToken);
        }

        return ended;
    }

    public static async Task<int> EndOtherSessionsAsync(ShelfDb db, int readerId, string? keep, CancellationToken cancellationToken = default)
    {
        var ended = await db.ReaderSessions.Where(item => item.ReaderId == readerId && item.Id != keep).ExecuteDeleteAsync(cancellationToken);
        if (ended > 0)
        {
            await Audit.NoteAsync(db, "Signed out every other device", await db.Readers.FindAsync([readerId], cancellationToken), $"{ended} signed out", cancellationToken);
        }

        return ended;
    }

    // A short name for a browser, from its User-Agent: enough to tell devices apart, nothing more.
    public static string DeviceName(string? userAgent)
    {
        var agent = userAgent ?? "";
        var browser = agent switch
        {
            _ when agent.Contains("Firefox/", StringComparison.Ordinal) => "Firefox",
            _ when agent.Contains("Edg/", StringComparison.Ordinal) => "Edge",
            _ when agent.Contains("Chrome/", StringComparison.Ordinal) => "Chrome",
            _ when agent.Contains("Safari/", StringComparison.Ordinal) => "Safari",
            _ when agent.Contains("KOReader", StringComparison.OrdinalIgnoreCase) => "KOReader",
            _ => "A browser",
        };
        var system = agent switch
        {
            _ when agent.Contains("Android", StringComparison.Ordinal) => "Android",
            _ when agent.Contains("iPhone", StringComparison.Ordinal) || agent.Contains("iPad", StringComparison.Ordinal) => "iOS",
            _ when agent.Contains("Windows", StringComparison.Ordinal) => "Windows",
            _ when agent.Contains("Mac OS", StringComparison.Ordinal) => "macOS",
            _ when agent.Contains("Linux", StringComparison.Ordinal) => "Linux",
            _ => null,
        };
        return system is null ? browser : $"{browser} on {system}";
    }

    private static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private static string Base32(byte[] bytes)
    {
        var output = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            output.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return output.ToString();
    }

    private static byte[] FromBase32(string text)
    {
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var character in text.TrimEnd('=').ToUpperInvariant())
        {
            var value = Alphabet.IndexOf(character);
            if (value < 0)
            {
                continue;
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. output];
    }
}

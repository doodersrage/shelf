using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Books;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Readers;

public sealed record EmailMessage(string To, string Subject, string Body, IReadOnlyList<EmailAttachment>? Attachments = null);

// A file sent with an email, read from disk as it goes.
public sealed record EmailAttachment(string FileName, string ContentType, string Path);

public interface IEmailSender
{
    // False when no mail server is set up; the shelf then offers nothing that needs email.
    bool Enabled { get; }

    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

public sealed class NoEmailSender : IEmailSender
{
    public bool Enabled => false;

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
}

// Sends with MailKit through the SMTP server in Email:Host, as Email:From, signing in with Email:User and
// Email:Password. Email:Security picks the connection: auto (the default), starttls, ssl, or none.
public sealed class SmtpEmailSender(IConfiguration configuration) : IEmailSender
{
    public bool Enabled => !string.IsNullOrWhiteSpace(configuration["Email:Host"]) && !string.IsNullOrWhiteSpace(configuration["Email:From"]);

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var mail = new MimeKit.MimeMessage();
        mail.From.Add(new MimeKit.MailboxAddress("Shelf", configuration["Email:From"]!));
        mail.To.Add(MimeKit.MailboxAddress.Parse(message.To));
        mail.Subject = message.Subject;
        var streams = new List<Stream>();
        if (message.Attachments is { Count: > 0 } attachments)
        {
            var body = new MimeKit.BodyBuilder { TextBody = message.Body };
            foreach (var attachment in attachments)
            {
                var stream = File.OpenRead(attachment.Path);
                streams.Add(stream);
                await body.Attachments.AddAsync(attachment.FileName, stream, MimeKit.ContentType.Parse(attachment.ContentType), cancellationToken);
            }

            mail.Body = body.ToMessageBody();
        }
        else
        {
            mail.Body = new MimeKit.TextPart("plain") { Text = message.Body };
        }

        using var client = new MailKit.Net.Smtp.SmtpClient();
        await client.ConnectAsync(configuration["Email:Host"]!, configuration.GetValue("Email:Port", 587), Security(), cancellationToken);
        if (configuration["Email:User"] is { Length: > 0 } user)
        {
            await client.AuthenticateAsync(user, configuration["Email:Password"] ?? "", cancellationToken);
        }

        try
        {
            await client.SendAsync(mail, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);
        }
        finally
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync();
            }
        }
    }

    private MailKit.Security.SecureSocketOptions Security() =>
        (configuration["Email:Security"] ?? (configuration.GetValue("Email:Ssl", true) ? "auto" : "none")).ToLowerInvariant() switch
        {
            "starttls" => MailKit.Security.SecureSocketOptions.StartTls,
            "ssl" => MailKit.Security.SecureSocketOptions.SslOnConnect,
            "none" => MailKit.Security.SecureSocketOptions.None,
            _ => MailKit.Security.SecureSocketOptions.Auto,
        };
}

// A link to reset a forgotten password: only its hash is kept, it works once, and it lasts an hour.
public sealed class PasswordReset
{
    public int Id { get; set; }
    public int ReaderId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Used { get; set; }
}

public static class EmailRules
{
    public const int MaxAddressLength = 254;
    public static readonly TimeSpan ResetLasts = TimeSpan.FromHours(1);
    public static readonly TimeSpan ConfirmLasts = TimeSpan.FromDays(1);

    // The shortest wait between two links to the same reader's new address, so an account cannot be made to mail
    // someone over and over.
    public static readonly TimeSpan ConfirmEvery = TimeSpan.FromMinutes(1);

    public static string? CleanAddress(string? address)
    {
        var trimmed = address?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= MaxAddressLength && MailAddress.TryCreate(trimmed, out var parsed) && parsed.Address == trimmed ? trimmed : "";
    }

    // Links in an email need the shelf's own address; Email:PublicAddress wins over the request's.
    public static string PublicAddress(IConfiguration configuration, HttpRequest? request) =>
        (configuration["Email:PublicAddress"] is { Length: > 0 } configured
            ? configured
            : request is null ? "" : $"{request.Scheme}://{request.Host}").TrimEnd('/');

    // Where a reset link may point. Never simply the address the request names: anyone can send a request naming
    // their own site, and the reset email would carry a working link to it. Email:PublicAddress, or the request's
    // host when AllowedHosts lists the shelf's names (ASP.NET refuses any other); otherwise none, and no link is sent.
    public static string? ResetAddress(IConfiguration configuration, HttpRequest request)
    {
        if (configuration["Email:PublicAddress"] is { Length: > 0 } configured)
        {
            return configured.TrimEnd('/');
        }

        var allowed = configuration["AllowedHosts"];
        return string.IsNullOrWhiteSpace(allowed) || allowed.Split(';').Any(host => host.Trim() == "*") ? null : $"{request.Scheme}://{request.Host}";
    }

    // Sends a reset link when the address, or the name a reader signs in with, is a reader's with an address. Says
    // nothing either way.
    public static async Task RequestResetAsync(
        ShelfDb db,
        IEmailSender email,
        string? who,
        string publicAddress,
        CancellationToken cancellationToken = default)
    {
        var wanted = who?.Trim() ?? "";
        if (!email.Enabled || wanted.Length == 0)
        {
            return;
        }

        var reader = await ReaderRules.FindAsync(db, wanted, cancellationToken);
        if (reader?.Email is null)
        {
            return;
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        db.PasswordResets.Add(new PasswordReset
        {
            ReaderId = reader.Id,
            TokenHash = Hash(token),
            ExpiresAt = DateTimeOffset.UtcNow.Add(ResetLasts),
        });
        await db.SaveChangesAsync(cancellationToken);
        // In the language the reader chose, or else the one the request came in.
        EmailMessage message;
        using (Localization.Words.Speaking(reader.Language ?? Localization.Words.Current))
        {
            message = new EmailMessage(
                reader.Email,
                Localization.Words.T("Reset your Shelf password"),
                Localization.Words.T("Hello {0},\n\nSomeone asked to reset the password for your shelf. If it was you, choose a new one here within the hour:\n\n{1}\n\nIf it was not you, ignore this email and your password stays as it is.", reader.Name, $"{publicAddress}/reset?token={token}"));
        }

        await email.SendAsync(message, cancellationToken);
    }

    public static async Task<AccountProblem?> ResetAsync(
        ShelfDb db,
        string? token,
        string? password,
        CancellationToken cancellationToken = default)
    {
        if (ReaderRules.PasswordProblem(password) is { } problem)
        {
            return problem;
        }

        var reset = await FindAsync(db, token, cancellationToken);
        if (reset is null)
        {
            return AccountProblem.ResetExpired;
        }

        reset.Used = true;
        await db.SaveChangesAsync(cancellationToken);
        await Audit.NoteAsync(db, Localization.Words.Say("Used a reset link from email"), await db.Readers.FindAsync([reset.ReaderId], cancellationToken), cancellationToken: cancellationToken);
        return await ReaderRules.SetPasswordAsync(db, reset.ReaderId, password!, cancellationToken)
            ? null
            : AccountProblem.ResetExpired;
    }

    public static async Task<bool> ResetIsLiveAsync(ShelfDb db, string? token, CancellationToken cancellationToken = default) =>
        await FindAsync(db, token, cancellationToken) is not null;

    private static async Task<PasswordReset?> FindAsync(ShelfDb db, string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            return null;
        }

        var hash = Hash(token.Trim());
        var now = DateTimeOffset.UtcNow;
        var reset = await db.PasswordResets.FirstOrDefaultAsync(item => item.TokenHash == hash && !item.Used, cancellationToken);
        return reset is not null && reset.ExpiresAt > now ? reset : null;
    }

    // A new address waits for its link to be opened when the shelf can send one, to an address of its own to link to.
    public static bool Confirms(IEmailSender email, string? linkAddress) => email.Enabled && !string.IsNullOrEmpty(linkAddress);

    // Mails the link that confirms the reader's pending address. False when there is none, or one went just now.
    public static async Task<bool> SendConfirmationAsync(ShelfDb db, IEmailSender email, Reader reader, string linkAddress, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (reader.PendingEmail is null || (reader.PendingEmailExpires is { } expires && expires - ConfirmLasts + ConfirmEvery > now))
        {
            return false;
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        (reader.PendingEmailTokenHash, reader.PendingEmailExpires) = (Hash(token), now + ConfirmLasts);
        await db.SaveChangesAsync(cancellationToken);
        EmailMessage message;
        using (Localization.Words.Speaking(reader.Language ?? Localization.Words.Current))
        {
            message = new EmailMessage(
                reader.PendingEmail,
                Localization.Words.T("Confirm your email address for Shelf"),
                Localization.Words.T("Hello {0},\n\nTo sign in to Shelf with this address, and to get your notifications here, open this link within a day:\n\n{1}\n\nIf you did not ask for this, ignore this email and nothing changes.", reader.Name, $"{linkAddress}/account/email/confirm?token={token}"));
        }

        await email.SendAsync(message, cancellationToken);
        return true;
    }

    // A reader's new address, and whether notifications go there. When the shelf confirms addresses, a new one waits
    // for its link (Pending) and the old one stays until then; otherwise it is theirs at once.
    public static async Task<(AccountProblem? Problem, bool Pending)> ChangeAsync(
        ShelfDb db,
        IEmailSender email,
        int readerId,
        string? address,
        bool notices,
        string? linkAddress,
        CancellationToken cancellationToken = default)
    {
        var clean = CleanAddress(address);
        var reader = await db.Readers.FirstOrDefaultAsync(item => item.Id == readerId, cancellationToken);
        if (!Confirms(email, linkAddress) || clean is null or "" || reader is null || string.Equals(clean, reader.Email, StringComparison.OrdinalIgnoreCase))
        {
            return (await ReaderRules.SetEmailAsync(db, readerId, address, notices, cancellationToken), false);
        }

        var normalized = ReaderRules.NormalizeEmail(clean);
        if (await db.Readers.AnyAsync(other => other.NormalizedEmail == normalized && other.Id != readerId, cancellationToken))
        {
            return (AccountProblem.EmailTaken, false);
        }

        var again = reader.PendingNormalizedEmail == normalized;
        (reader.PendingEmail, reader.PendingNormalizedEmail, reader.EmailNotices) = (clean, normalized, notices);
        if (!again)
        {
            // A different address than the one waiting: its link goes now, and the old link no longer works.
            (reader.PendingEmailTokenHash, reader.PendingEmailExpires) = (null, null);
        }

        await db.SaveChangesAsync(cancellationToken);
        await SendConfirmationAsync(db, email, reader, linkAddress!, cancellationToken);
        return (null, true);
    }

    // Opening the link: the pending address becomes the reader's, unless someone confirmed it first.
    public static async Task<AccountProblem?> ConfirmAsync(ShelfDb db, string? token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            return AccountProblem.ConfirmExpired;
        }

        var hash = Hash(token.Trim());
        var reader = await db.Readers.FirstOrDefaultAsync(item => item.PendingEmailTokenHash == hash, cancellationToken);
        if (reader is not { PendingEmail: { } pending, PendingNormalizedEmail: { } normalized } || reader.PendingEmailExpires < DateTimeOffset.UtcNow)
        {
            return AccountProblem.ConfirmExpired;
        }

        (reader.PendingEmail, reader.PendingNormalizedEmail, reader.PendingEmailTokenHash, reader.PendingEmailExpires) = (null, null, null, null);
        if (await db.Readers.AnyAsync(other => other.NormalizedEmail == normalized && other.Id != reader.Id, cancellationToken))
        {
            await db.SaveChangesAsync(cancellationToken);
            return AccountProblem.EmailTaken;
        }

        (reader.Email, reader.NormalizedEmail) = (pending, normalized);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return AccountProblem.EmailTaken;
        }

        await Audit.NoteAsync(db, Localization.Words.Say("Confirmed the email address"), reader, pending, cancellationToken);
        return null;
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}

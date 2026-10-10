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

    // Sends a reset link when the name or address belongs to a reader with an address. Says nothing either way.
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

        var normalized = ReaderRules.Normalize(wanted);
        var reader = await db.Readers.FirstOrDefaultAsync(
            item => item.Email != null && (item.NormalizedName == normalized || item.Email.ToLower() == wanted.ToLower()),
            cancellationToken);
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

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static string? ReminderBody(string name, Reminders due, string publicAddress)
    {
        if (!due.Any)
        {
            return null;
        }

        var lines = new List<string>();
        if (due.LentOverdue > 0)
        {
            lines.Add("- " + (due.LentOverdue == 1 ? T("1 book you lent is overdue.") : T("{0} books you lent are overdue.", due.LentOverdue)));
        }

        if (due.BorrowedOverdue > 0)
        {
            lines.Add("- " + (due.BorrowedOverdue == 1 ? T("1 book you borrowed is overdue.") : T("{0} books you borrowed are overdue.", due.BorrowedOverdue)));
        }

        if (due.BorrowedDueSoon > 0)
        {
            lines.Add("- " + (due.BorrowedDueSoon == 1 ? T("1 borrowed book is due within three days.") : T("{0} borrowed books are due within three days.", due.BorrowedDueSoon)));
        }

        if (due.Asks > 0)
        {
            lines.Add("- " + (due.Asks == 1 ? T("1 reader has asked to borrow a book of yours.") : T("{0} readers have asked to borrow a book of yours.", due.Asks)));
        }

        var link = publicAddress.Length > 0 ? T("See your loans: {0}", $"{publicAddress}/loans") : T("Open Shelf and go to Loans to see them.");
        return T("Hello {0},", name) + "\n\n" + string.Join('\n', lines) + "\n\n" + link + "\n\n" + T("You can turn these reminders off from your account.");
    }
}

// Once a day, a short email to each reader who asked for one, when a loan needs their attention.
public sealed class ReminderMailer(IServiceScopeFactory scopes, IEmailSender email, IConfiguration configuration, ILogger<ReminderMailer> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!email.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await SendDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Reminder emails stopped; they will try again in an hour.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> SendDueAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        List<int> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            due = await db.Readers.AsNoTracking()
                .Where(reader => reader.EmailReminders && reader.Email != null && (reader.LastReminderOn == null || reader.LastReminderOn < today))
                .Select(reader => reader.Id)
                .ToListAsync(cancellationToken);
        }

        var sent = 0;
        foreach (var readerId in due)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var reader = await db.Readers.FirstAsync(item => item.Id == readerId, cancellationToken);
            var reminders = await Asking.RemindersAsync(db, cancellationToken);
            EmailMessage? message = null;
            using (Localization.Words.Speaking(reader.Language))
            {
                if (EmailRules.ReminderBody(reader.Name, reminders, EmailRules.PublicAddress(configuration, null)) is { } body)
                {
                    message = new EmailMessage(reader.Email!, Localization.Words.T("Your loans on Shelf"), body);
                }
            }

            if (message is not null)
            {
                await email.SendAsync(message, cancellationToken);
                sent++;
            }

            // Checked today either way, so a quiet day sends nothing until tomorrow.
            reader.LastReminderOn = today;
            await db.SaveChangesAsync(cancellationToken);
        }

        return sent;
    }
}

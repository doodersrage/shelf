namespace Shelf.Api.Readers;

public sealed class Reader
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public string PasswordHash { get; set; } = "";
    public string? KeyHash { get; set; }

    // A hash of the MD5 KOReader sends for its own sync password.
    public string? KosyncHash { get; set; }

    // The account at the single sign-on provider this reader signs in with, as issuer|subject; never an email,
    // which a provider may let someone else claim.
    public string? OidcSubject { get; set; }

    // Made by single sign-on with a random password nobody knows: setting one then needs no current password.
    public bool PasswordUnknown { get; set; }

    // The random id a passkey carries for this reader, made with the first one.
    public byte[]? PasskeyHandle { get; set; }

    // The language of the pages for this reader (en, es, fr, de); without one, the browser's languages decide.
    public string? Language { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // An admin can reset passwords and remove readers. The first reader is one.
    public bool IsAdmin { get; set; }

    // Other readers may see this shelf and ask to borrow from it.
    public bool ShelfOpen { get; set; }

    // Changes when the password is reset or changed, which signs out every session made before.
    public string Stamp { get; set; } = ReaderRules.NewStamp();

    // Optional, for password resets and, when EmailReminders is on, a daily note about loans.
    public string? Email { get; set; }
    public bool EmailReminders { get; set; }
    public DateOnly? LastReminderOn { get; set; }

    // Where Send to Kindle emails this reader's e-books: their Kindle's own address at Amazon.
    public string? KindleEmail { get; set; }

    // Readwise: the access token, sealed with the shelf's keys; whether new highlights go there on their own; and
    // when they last went.
    public string? ReadwiseToken { get; set; }
    public bool ReadwiseAuto { get; set; }
    public DateTimeOffset? ReadwiseSentAt { get; set; }

    // Hardcover: the API token, sealed with the shelf's keys, and whether changes go there on their own.
    public string? HardcoverToken { get; set; }
    public bool HardcoverAuto { get; set; }

    // Kobo sync: a hash of the token in the Kobo's address for Shelf, and the collection it gets (all e-books when none).
    public string? KoboTokenHash { get; set; }
    public int? KoboCollectionId { get; set; }

    // Two-step sign-in: the authenticator secret, sealed with the shelf's own keys.
    public bool TwoFactorEnabled { get; set; }
    public string? TwoFactorSecret { get; set; }

    // How dates and numbers are written for this reader, such as en-GB; null follows the browser.
    public string? Culture { get; set; }
}

public sealed record TwoFactorStart(string Secret, string Uri);

public sealed record TwoFactorCodeRequest(string? Code);

public sealed record RecoveryCodes(string[] Codes);

public sealed record EmailSettingsRequest(string? Email, bool Reminders);

public sealed record KindleSettingsRequest(string? Email);

public sealed record ReaderResponse(int Id, string Name);

public sealed record ReaderSummary(int Id, string Name, bool IsAdmin, DateTimeOffset CreatedAt, int Books);

public sealed record AdminChange(bool IsAdmin);

public sealed record RemoveAccountRequest(string? Password);

public sealed record ShelfOpenChange(bool Open);

public sealed record TemporaryPassword(string Password);

public sealed record OpenShelf(int Id, string Name, int Books);

public sealed record ShelfBook(
    int Id,
    string Title,
    string Author,
    string? CoverUrl,
    int? Year,
    bool OnLoan,
    bool HasEbook,
    bool HasAudio,
    bool Asked);

// A book on another reader's open shelf: what the borrowing hints need to match it and offer to ask.
public sealed record OpenCopy(
    int Id,
    int OwnerId,
    string Owner,
    string Title,
    string Author,
    string? Isbn,
    bool OnLoan,
    bool HasEbook,
    bool HasAudio,
    bool Asked);

public sealed record AskRequest(int BookId);

public sealed record LoanAsk(
    int Id,
    int BookId,
    string Title,
    string Author,
    int ReaderId,
    string Reader,
    DateTimeOffset AskedAt,
    bool OnLoan);

public sealed record LoanAsks(LoanAsk[] Incoming, LoanAsk[] Outgoing);

public sealed record LendAskRequest(DateOnly? DueOn = null);

public sealed record Reminders(int LentOverdue, int BorrowedOverdue, int BorrowedDueSoon, int Asks)
{
    public bool Any => LentOverdue + BorrowedOverdue + BorrowedDueSoon + Asks > 0;
}

public sealed record LendRequest(int ReaderId, DateOnly? DueOn = null);

public sealed record BorrowedBook(
    int Id,
    string Title,
    string Author,
    string? CoverUrl,
    string Owner,
    DateOnly? LoanedOn,
    DateOnly? DueOn,
    bool HasEbook = false,
    bool HasAudio = false);

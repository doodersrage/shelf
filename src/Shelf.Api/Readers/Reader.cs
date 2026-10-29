namespace Shelf.Api.Readers;

public sealed class Reader
{
    public int Id { get; set; }
    // The name other readers see. Names may repeat; a reader without an email address signs in with theirs, so
    // among those readers each name is still unique.
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

    // Where the reader is, as an IANA time zone, for the days they read on; without one, the server's (see ReaderClock).
    public string? TimeZone { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // An admin can reset passwords and remove readers. The first reader is one.
    public bool IsAdmin { get; set; }

    // A child's account, made by their guardian (see Family): it reads what its guardian shares, and can do less.
    public bool Restricted { get; set; }
    public int? GuardianId { get; set; }

    // Suspended by an admin: no way of signing in works, and the shelf is kept as it is.
    public bool Suspended { get; set; }

    // Other readers may see this shelf and ask to borrow from it.
    public bool ShelfOpen { get; set; }

    // The newer release an admin said "not now" to.
    public string? DismissedRelease { get; set; }

    // The pages left out of the reader's menu, their addresses one to a line (NavMap).
    public string? HiddenNav { get; set; }

    // Changes when the password is reset or changed, which signs out every session made before.
    public string Stamp { get; set; } = ReaderRules.NewStamp();

    // Changes when the reader signs out everywhere else, which signs out the Audiobookshelf apps signed in before
    // while leaving this browser signed in, as changing Stamp would not. Null until it first changes.
    public string? AppStamp { get; set; }

    // The address the reader signs in with, unique on the shelf, and where password resets and, when EmailNotices
    // is on and the shelf can send email, notifications go. Children's accounts, and accounts made before sign-in
    // was by email, may have none and sign in by name.
    public string? Email { get; set; }

    // The address upper-cased for finding the reader by it; null when the reader does not sign in with it.
    public string? NormalizedEmail { get; set; }
    public bool EmailNotices { get; set; } = true;

    // An address given but not yet confirmed: a link went to it, and it becomes Email when the link is opened. Until
    // then it is no one's, so giving someone else's address takes it from no one.
    public string? PendingEmail { get; set; }
    public string? PendingNormalizedEmail { get; set; }
    public string? PendingEmailTokenHash { get; set; }
    public DateTimeOffset? PendingEmailExpires { get; set; }

    // Where Send to Kindle emails this reader's e-books: their Kindle's own address at Amazon.
    public string? KindleEmail { get; set; }

    // Readwise: the access token, sealed with the shelf's keys; whether new highlights go there on their own; and
    // when they last went.
    public string? ReadwiseToken { get; set; }
    public bool ReadwiseAuto { get; set; }
    public DateTimeOffset? ReadwiseSentAt { get; set; }

    // And the other way: highlights made elsewhere brought from Readwise, on their own every hour, and when last.
    public bool ReadwiseAutoImport { get; set; }
    public DateTimeOffset? ReadwiseImportedAt { get; set; }

    // Why the last trade with Readwise went wrong, and when; cleared when one goes right.
    public Books.SyncProblem? ReadwiseProblem { get; set; }
    public DateTimeOffset? ReadwiseProblemAt { get; set; }

    // Hardcover: the API token, sealed with the shelf's keys, and whether changes go there on their own.
    public string? HardcoverToken { get; set; }
    public bool HardcoverAuto { get; set; }

    // Why the last trade with Hardcover went wrong, and when; cleared when one goes right.
    public Books.SyncProblem? HardcoverProblem { get; set; }
    public DateTimeOffset? HardcoverProblemAt { get; set; }

    // Whether books only on Hardcover are added here when its changes are brought back.
    public bool HardcoverAddMissing { get; set; }

    // Kobo sync: a hash of the token in the Kobo's address for Shelf, and the collection it gets (all e-books when none).
    public string? KoboTokenHash { get; set; }
    public int? KoboCollectionId { get; set; }

    // Notifications: which kinds (see NoticeKinds), an ntfy topic and a Gotify server with their tokens sealed, and
    // the day loans were last looked at for a reminder.
    public int NoticeKinds { get; set; } = (int)Readers.NoticeKinds.All;
    public string? NtfyUrl { get; set; }
    public string? NtfyToken { get; set; }
    public string? GotifyUrl { get; set; }
    public string? GotifyToken { get; set; }
    public DateOnly? LastNoticeOn { get; set; }

    // The devices this reader has signed in on, by a hash of each one's name, newest first; a sign-in from any other
    // is emailed to them.
    public string? KnownDevices { get; set; }

    // For an admin: the problems on Server health they were last told of, so each is told once.
    public string? HealthTold { get; set; }

    // How many days before a loan is due the reminder speaks of it.
    public int DueSoonDays { get; set; } = DefaultDueSoonDays;

    public const int DefaultDueSoonDays = 3;

    // A sandbox of sample books made by this reader, for trying a connector on; no one signs in as it.
    public int? SandboxOf { get; set; }

    // The most space this reader's files may take, set by an admin; null for no limit.
    public int? StorageLimitMegabytes { get; set; }

    // A hash of the token in this reader's calendar address, and the token in their public page's address, when on.
    public string? CalendarTokenHash { get; set; }
    public string? PublicToken { get; set; }

    // Two-step sign-in: the authenticator secret, sealed with the shelf's own keys.
    public bool TwoFactorEnabled { get; set; }
    public string? TwoFactorSecret { get; set; }

    // The time step of the last authenticator code taken, so each code works once: never again, even within its window.
    public long? TwoFactorLastStep { get; set; }

    // How dates and numbers are written for this reader, such as en-GB; null follows the browser.
    public string? Culture { get; set; }
}

public sealed record TwoFactorStart(string Secret, string Uri);

public sealed record TwoFactorCodeRequest(string? Code);

public sealed record RecoveryCodes(string[] Codes);

// Reminders: whether notifications go to the address too (the name is kept from when it meant only loan reminders).
public sealed record EmailSettingsRequest(string? Email, bool Reminders);

public sealed record KindleSettingsRequest(string? Email);

public sealed record ReaderResponse(int Id, string Name);

public sealed record ReaderSummary(int Id, string Name, bool IsAdmin, DateTimeOffset CreatedAt, int Books, bool Child = false, string? Email = null);

public sealed record AdminChange(bool IsAdmin);

public sealed record RemoveAccountRequest(string? Password);

public sealed record ShelfOpenChange(bool Open);

public sealed record TemporaryPassword(string Password);

public sealed record OpenShelf(int Id, string Name, int Books);

public sealed record OpenShelfPage(string Owner, int Total, ShelfBook[] Books, OpenShelfCounts Counts);

// How many of the books matching a search are there in all, have an e-book or audiobook, or are not lent out now.
public sealed record OpenShelfCounts(int All, int Ebook, int Audio, int Available);

// What another reader's open shelf is narrowed to and ordered by: only what the owner shows, never their reading.
public sealed record OpenShelfFilter(string? Search = null, string? Sort = null, string? Has = null, string? Author = null, string? Series = null)
{
    public static readonly string[] Sorts = ["author", "title", "series", "year", "added"];

    public static readonly string[] Kinds = ["ebook", "audio", "available"];
}

public sealed record ShelfBook(
    int Id,
    string Title,
    string Author,
    string? CoverUrl,
    int? Year,
    bool OnLoan,
    bool HasEbook,
    bool HasAudio,
    bool Asked,
    string? Series = null,
    int? SeriesNumber = null);

// A book lately put on another reader's open shelf, with whose shelf it is: only what an open shelf shows anyone.
public sealed record OpenShelfBook(int Id, string Title, string Author, string? CoverUrl, bool OnLoan, int OwnerId, string Owner);

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

public sealed record Reminders(int LentOverdue, int BorrowedOverdue, int BorrowedDueSoon, int Asks, int SoonDays = Reader.DefaultDueSoonDays)
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
    bool HasAudio = false,
    bool Linked = false);

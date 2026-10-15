using static Shelf.Api.Localization.Words;

namespace Shelf.Api;

// A guide in Shelf's documentation: its page, its name, and what it helps with. AdminOnly guides are about running a
// shelf rather than using one.
public sealed record Guide(string Page, string Title, string About, bool AdminOnly = false);

// A part of a guide: its page, and a heading's anchor on it.
public sealed record DocsPlace(string Page, string Section);

// Shelf's documentation, online, for readers who need a hand finding their way: the Help page lists its guides, a "?"
// beside a page's heading opens the part about that page, and Server health links each fix to how to make it. A
// shelf that keeps its own copy of the documentation sets DocsUrl to it.
public static class Docs
{
    public const string Site = "https://doodersrage.github.io/shelf/";

    public static string Url(IConfiguration configuration, string page, string? section = null)
    {
        var root = configuration["DocsUrl"] is { Length: > 0 } own && Uri.TryCreate(own, UriKind.Absolute, out var parsed) && parsed.Scheme is "https" or "http"
            ? own.TrimEnd('/') + "/"
            : Site;
        return root + (page == "index" ? "" : page + ".html") + (section is null ? "" : "#" + section);
    }

    // The guides, in the order a reader would look for them. Names are asked for each time, in the reader's language.
    public static IReadOnlyList<Guide> Guides() =>
    [
        new("features", T("What Shelf does"), T("Every part of Shelf, with pictures, to see what it can do.")),
        new("library", T("Your library"), T("Home, the library and its views, statuses, finding books, collections, and the reading log.")),
        new("adding", T("Bringing books in"), T("Adding books by hand or by scanning them, from files, and from other apps.")),
        new("reading", T("Reading"), T("The e-book reader, PDFs and comics, highlights and notes, read aloud, and reading offline.")),
        new("audiobooks", T("Audiobooks"), T("The player, chapters and bookmarks, the sleep timer, read-along, and podcasts.")),
        new("lending", T("Readers and lending"), T("Lending and borrowing, asks to borrow, children's accounts, and book clubs.")),
        new("devices", T("Devices and apps"), T("Kobo, KOReader, Kindle, Audiobookshelf's phone apps, Readwise, and Hardcover.")),
        new("notifications", T("Notifications"), T("What Shelf tells you about, and how: by email, on this device, or by ntfy or Gotify.")),
        new("security", T("Accounts and security"), T("Signing in, passkeys, two-step sign-in, and keeping your account safe.")),
        new("install", T("Installing and updating"), T("Putting Shelf on a server, and updating it to a newer release."), AdminOnly: true),
        new("configuration", T("Configuration"), T("Every setting: email, sign-up, Turnstile, backups, and the rest."), AdminOnly: true),
        new("backups", T("Backups"), T("Nightly backups, snapshots, and bringing a shelf back."), AdminOnly: true),
        new("server", T("Running on a server"), T("A reverse proxy, OCR, disk space, and what to do when something goes wrong."), AdminOnly: true),
        new("api", T("The API"), T("Scripts and dashboards that use the shelf through its API."), AdminOnly: true),
    ];

    // Every place in the documentation the app links to, so a test can check each still exists.
    public static IReadOnlyList<(string Page, string? Section)> Linked =>
    [
        .. Guides().Select(guide => (guide.Page, (string?)null)),
        ("security", "signing-in"),
        ("security", "two-step-sign-in"),
        ("library", "trash-and-undo"),
        ("adding", "scanning-a-pile-of-books"),
        ("reading", "where-you-stopped"),
        ("audiobooks", "the-player"),
        ("audiobooks", "podcasts"),
        ("lending", "lending-to-another-reader"),
        ("lending", "book-clubs"),
        ("notifications", "what-you-hear-about"),
        ("security", "admins"),
        ("backups", "a-reader-39-s-full-backup"),
        ("backups", "health-checks"),
        ("backups", "checking-files"),
        ("configuration", "email"),
        ("configuration", "cloudflare-turnstile"),
        ("configuration", "nightly-backups"),
        ("server", "ocr-for-scanned-pdfs"),
        ("server", "disk-space"),
        ("install", "updating"),
    ];
}

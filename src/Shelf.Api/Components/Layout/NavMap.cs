using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Components.Layout;

// Who may see a page: anyone, only grown-up readers (not a child's account), only admins, or only where fetching
// books from indexers is set up.
public enum NavNeed
{
    Anyone,
    Grown,
    Admin,
    Fetching,
}

// A page to go to: its address, its name, other words (in English) it may be looked for by, and who may see it.
public sealed record NavLink(string Href, string Label, string? Also = null, NavNeed Need = NavNeed.Anyone);

// A group of pages in the menu. A group that can be hidden can have any of its pages taken out of the menu.
public sealed record NavGroup(string Key, string Label, IReadOnlyList<NavLink> Links, bool Hideable = true);

// Every page of Shelf, in one place: the menu is drawn from it, quick jump finds pages in it, and a reader chooses
// from it which pages to leave out of their menu. Names are asked for each time, in the reader's own language.
public static class NavMap
{
    public const int MaxHidden = 80;

    // The five always shown at the top of the menu and along the bottom on a phone.
    public static IReadOnlyList<NavLink> Top() =>
    [
        new("/", T("Home"), "dashboard start"),
        new("/library", T("Library"), "books catalog all"),
        new("/reading", T("Reading"), "currently reading progress"),
        new("/loans", T("Loans"), "lend borrow lent borrowed"),
        new("/stats", T("Insights"), "statistics stats charts"),
    ];

    public static IReadOnlyList<NavGroup> Groups() =>
    [
        new("library", T("Your books"),
        [
            new("/add", T("Add & import"), "add new book import goodreads storygraph calibre audiobookshelf spreadsheet csv scan isbn upload"),
            new("/library?status=Want", T("Want to read"), "to read wishlist tbr"),
            new("/queue", T("Up next"), "queue holds"),
            new("/library?status=Finished", T("Finished"), "read done"),
            new("/collections", T("Collections"), "lists"),
            new("/series", T("Series")),
            new("/authors", T("Authors"), "writers follow"),
            new("/map", T("Map"), "places settings world"),
        ]),
        new("reading", T("Your reading"),
        [
            new("/journal", T("Journal"), "diary notes"),
            new("/quotes", T("Quotes & highlights"), "highlights passages readwise"),
            new("/words", T("Words and passages"), "vocabulary translation language"),
            new("/agenda", T("Calendar"), "agenda ics plans goals"),
            new("/clubs", T("Book clubs"), "club meetings"),
            new("/research", T("Reference desk"), "research questions bibliography"),
        ]),
        new("discover", T("Discover"),
        [
            new("/find", T("Find new books"), "open library search new"),
            new("/next", T("What to read next"), "recommend suggestions"),
            new("/free", T("Free books"), "gutenberg librivox public domain"),
            new("/podcasts", T("Podcasts"), "rss feeds episodes"),
            new("/ask", T("Ask your shelf"), "question ai concierge"),
            new("/search", T("Search inside books"), "full text"),
            new("/wanted", T("Wanted"), "indexers download fetch", NavNeed.Fetching),
        ]),
        new("insights", T("Insights"),
        [
            new("/years", T("Years"), "year in review wrapped"),
            new("/dna", T("Reading DNA"), "habits taste"),
            new("/lifecycle", T("Your collection over time"), "cost spent value lifecycle"),
        ]),
        new("copies", T("Your copies"),
        [
            new("/shelves", T("Shelves"), "bookcases linked", NavNeed.Grown),
            new("/places", T("Places"), "rooms bookcases boxes location storage"),
            new("/moving", T("Moving house"), "move boxes pack unpack"),
            new("/scan", T("Scan books"), "barcode isbn camera spines photo check"),
            new("/labels", T("QR labels"), "print qr codes"),
            new("/copies", T("Condition"), "condition copies"),
            new("/recommenders", T("Recommenders"), "who recommended"),
            new("/duplicates", T("Duplicates"), "doubles merge"),
            new("/tidy", T("Tidy up"), "clean covers authors publishers fix"),
        ]),
        new("settings", T("Settings"),
        [
            new("/account", T("Account"), "settings profile preferences"),
            new("/activity", T("Activity"), "log recent"),
            new("/backup", T("Backup & restore"), "export download restore"),
            new("/trash", T("Trash & undo"), "deleted undo history changes"),
            new("/rules", T("Rules"), "automations automatic", NavNeed.Grown),
            new("/account/legacy", T("Your library's future"), "legacy estate plan", NavNeed.Grown),
            new("/sync", T("Devices"), "kobo koreader sync opds e-reader"),
            new("/offline.html", T("Offline reading"), "offline"),
        ], Hideable: false),
        new("admin", T("Admin"),
        [
            new("/admin", T("Readers"), "users accounts invite", NavNeed.Admin),
            new("/admin/health", T("Server health"), "disk storage status", NavNeed.Admin),
            new("/admin/access", T("Who sees what"), "permissions access preview", NavNeed.Admin),
            new("/downloads", T("Downloads"), "indexers download clients", NavNeed.Admin),
        ], Hideable: false),
    ];

    // Pages and parts of pages not in the menu, for quick jump to find.
    public static IReadOnlyList<NavLink> Elsewhere() =>
    [
        new("/library?add=1", T("Add a book"), "new isbn type"),
        new("/backup#csv", T("Bring a library from Goodreads or StoryGraph"), "csv export"),
        new("/import/spreadsheet", T("Bring in any spreadsheet"), "csv excel columns"),
        new("/import/calibre", T("Bring books from Calibre")),
        new("/import/audiobookshelf", T("Bring books from Audiobookshelf"), "abs"),
        new("/account#look", T("Look"), "theme dark light colours"),
        new("/account#language", T("Language and region"), "translation culture date"),
        new("/account#password", T("Change the password"), "password"),
        new("/account#two-step", T("Two-step sign-in"), "2fa totp authenticator"),
        new("/account#passkeys", T("Passkeys"), "webauthn fido"),
        new("/account#tokens", T("API tokens"), "api key"),
        new("/account#notices", T("Notifications"), "ntfy gotify email reminders"),
        new("/account#kindle", T("Send to Kindle"), "kindle email"),
        new("/account#readwise", "Readwise"),
        new("/account#hardcover", "Hardcover"),
        new("/account#webhooks", T("Webhooks"), "hooks"),
        new("/account#feeds", T("Calendar and public page"), "ics feed public"),
        new("/account#menu", T("Menu"), "hide pages sidebar"),
        new("/account#children", T("Children's accounts"), "child family"),
        new("/account#sandbox", T("Sandbox"), "sample test"),
    ];

    // Whether a reader may see a page.
    public static bool Allowed(NavLink link, bool admin, bool child, bool fetching) => link.Need switch
    {
        NavNeed.Admin => admin,
        NavNeed.Grown => !child,
        NavNeed.Fetching => fetching && !child,
        _ => true,
    };

    // The pages a reader has left out of their menu, kept as their addresses one to a line.
    public static HashSet<string> Hidden(string? stored) =>
        [.. (stored ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    public static string? Store(IEnumerable<string> hidden)
    {
        var hideable = Groups().Where(group => group.Hideable).SelectMany(group => group.Links).Select(link => link.Href).ToHashSet();
        var kept = hidden.Where(hideable.Contains).Distinct().Take(MaxHidden).Order(StringComparer.Ordinal).ToList();
        return kept.Count == 0 ? null : string.Join('\n', kept);
    }
}

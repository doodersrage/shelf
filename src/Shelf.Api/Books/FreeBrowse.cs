using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Shelf.Api.Books;

// More about a free book than a list shows: its summary or description, subjects, and where it came from.
public sealed record FreeDetails(
    string? About,
    IReadOnlyList<string> Subjects,
    string? Language,
    string? Length,
    string? Published,
    int? Downloads,
    string? ReadingLevel,
    int? Chapters,
    string? Translators,
    string Link,
    string? CoverUrl);

public sealed record FreePage(List<FreeBook> Books, bool More);

public sealed record FreeShelf(string Key, string Name);

// Browsing the free catalogs without a search: Gutenberg's most read books and its categories, LibriVox's newest
// recordings and its genres, a page at a time; and the details of one book.
public static partial class FreeCatalog
{
    private const int LibriVoxPage = 24;

    // Gutenberg's own categories ("Books in Category: …"), which it numbers 633 to 704.
    public static readonly IReadOnlyList<FreeShelf> GutenbergShelves =
    [
        new("649", Localization.Words.Say("Classics of Literature")), new("645", Localization.Words.Say("Novels")), new("644", Localization.Words.Say("Adventure")), new("640", Localization.Words.Say("Crime, Thrillers and Mystery")),
        new("638", Localization.Words.Say("Science-Fiction & Fantasy")), new("639", Localization.Words.Say("Romance")), new("635", Localization.Words.Say("Historical Novels")), new("634", Localization.Words.Say("Short Stories")),
        new("637", Localization.Words.Say("Poetry")), new("642", Localization.Words.Say("Plays/Films/Dramas")), new("641", Localization.Words.Say("Humour")), new("636", Localization.Words.Say("Children & Young Adult Reading")),
        new("646", Localization.Words.Say("Mythology, Legends & Folklore")), new("647", Localization.Words.Say("Essays, Letters & Speeches")), new("648", Localization.Words.Say("Travel Writing")), new("643", Localization.Words.Say("Biographies")),
        new("653", Localization.Words.Say("British Literature")), new("654", Localization.Words.Say("American Literature")), new("652", Localization.Words.Say("French Literature")), new("651", Localization.Words.Say("German Literature")),
        new("650", Localization.Words.Say("Russian Literature")), new("633", Localization.Words.Say("Literature - Other")), new("705", Localization.Words.Say("Nobel Prizes in Literature")),
        new("659", Localization.Words.Say("History - Ancient")), new("660", Localization.Words.Say("History - Medieval/Middle Ages")), new("661", Localization.Words.Say("History - Early Modern (c. 1450-1750)")),
        new("662", Localization.Words.Say("History - Modern (1750+)")), new("656", Localization.Words.Say("History - American")), new("657", Localization.Words.Say("History - British")), new("658", Localization.Words.Say("History - European")),
        new("663", Localization.Words.Say("History - Religious")), new("664", Localization.Words.Say("History - Royalty")), new("665", Localization.Words.Say("History - Warfare")), new("655", Localization.Words.Say("History - Other")),
        new("691", Localization.Words.Say("Philosophy & Ethics")), new("692", Localization.Words.Say("Religion/Spirituality")), new("688", Localization.Words.Say("Psychiatry/Psychology")), new("693", Localization.Words.Say("Sociology")),
        new("694", Localization.Words.Say("Politics")), new("696", Localization.Words.Say("Economics")), new("689", Localization.Words.Say("Law & Criminology")), new("687", Localization.Words.Say("Language & Communication")),
        new("667", Localization.Words.Say("Science - Physics")), new("668", Localization.Words.Say("Science - Chemistry/Biochemistry")), new("669", Localization.Words.Say("Science - Biology")),
        new("670", Localization.Words.Say("Science - Earth/Agricultural/Farming")), new("672", Localization.Words.Say("Mathematics")), new("671", Localization.Words.Say("Engineering & Technology")),
        new("683", Localization.Words.Say("Nature/Gardening/Animals")), new("681", Localization.Words.Say("Health & Medicine")), new("675", Localization.Words.Say("Art")), new("677", Localization.Words.Say("Music")), new("674", Localization.Words.Say("Architecture")),
        new("676", Localization.Words.Say("Fashion")), new("678", Localization.Words.Say("Cooking & Drinking")), new("680", Localization.Words.Say("Sports/Hobbies")), new("679", Localization.Words.Say("How To ...")),
        new("697", Localization.Words.Say("Encyclopedias/Dictionaries/Reference")), new("704", Localization.Words.Say("Teaching & Education")), new("701", Localization.Words.Say("Parenthood & Family Relations")),
    ];

    // LibriVox's genres, by the exact names its catalog filters on.
    public static readonly IReadOnlyList<FreeShelf> LibriVoxGenres =
    [
        new("Literary Fiction", Localization.Words.Say("Literary Fiction")), new("Action & Adventure Fiction", Localization.Words.Say("Action & Adventure Fiction")),
        new("Crime & Mystery Fiction", Localization.Words.Say("Crime & Mystery Fiction")), new("Detective Fiction", Localization.Words.Say("Detective Fiction")),
        new("Science Fiction", Localization.Words.Say("Science Fiction")), new("Fantasy Fiction", Localization.Words.Say("Fantasy Fiction")), new("Horror & Supernatural Fiction", Localization.Words.Say("Horror & Supernatural Fiction")),
        new("Gothic Fiction", Localization.Words.Say("Gothic Fiction")), new("Historical Fiction", Localization.Words.Say("Historical Fiction")), new("Romance", Localization.Words.Say("Romance")),
        new("Humorous Fiction", Localization.Words.Say("Humorous Fiction")), new("Satire", Localization.Words.Say("Satire")), new("Short Stories", Localization.Words.Say("Short Stories")),
        new("Children's Fiction", Localization.Words.Say("Children's Fiction")), new("Myths, Legends & Fairy Tales", Localization.Words.Say("Myths, Legends & Fairy Tales")),
        new("Nautical & Marine Fiction", Localization.Words.Say("Nautical & Marine Fiction")), new("War & Military Fiction", Localization.Words.Say("War & Military Fiction")),
        new("Westerns", Localization.Words.Say("Westerns")), new("Epistolary Fiction", Localization.Words.Say("Epistolary Fiction")), new("Family Life", Localization.Words.Say("Family Life")),
        new("Poetry", Localization.Words.Say("Poetry")), new("Drama", Localization.Words.Say("Drama")), new("Classics (Greek & Latin Antiquity)", Localization.Words.Say("Classics (Greek & Latin Antiquity)")),
        new("Biography & Autobiography", Localization.Words.Say("Biography & Autobiography")), new("Letters", Localization.Words.Say("Letters")), new("History", Localization.Words.Say("History")),
        new("Philosophy", Localization.Words.Say("Philosophy")), new("Religion", Localization.Words.Say("Religion")), new("Psychology", Localization.Words.Say("Psychology")), new("Science", Localization.Words.Say("Science")),
        new("Travel & Geography", Localization.Words.Say("Travel & Geography")), new("Essays & Short Works", Localization.Words.Say("Essays & Short Works")), new("Self-Help", Localization.Words.Say("Self-Help")),
    ];

    // The most downloaded books, or one category, twenty-five at a time; Gutenberg says when there is a next page.
    public static async Task<FreePage> BrowseGutenbergAsync(HttpClient http, string? shelf, int start, CancellationToken cancellationToken)
    {
        var url = shelf is not null && GutenbergShelves.Any(item => item.Key == shelf)
            ? $"{Gutenberg}/ebooks/bookshelf/{shelf}.opds?start_index={start}"
            : $"{Gutenberg}/ebooks/search.opds/?sort_order=downloads&start_index={start}";
        var (books, more) = await ReadGutenbergFeedAsync(http, url, cancellationToken);
        return new FreePage(books, more);
    }

    // One genre, or the newest recordings (the last ninety days, newest first), a page at a time.
    public static async Task<FreePage> BrowseLibriVoxAsync(HttpClient http, string? genre, int offset, CancellationToken cancellationToken)
    {
        const string fields = "fields=%7Bid,title,authors,language,totaltime,url_zip_file%7D";
        if (genre is not null && LibriVoxGenres.Any(item => item.Key == genre))
        {
            var books = await ReadLibriVoxAsync(http, $"https://librivox.org/api/feed/audiobooks/?genre={Uri.EscapeDataString(genre)}&format=json&limit={LibriVoxPage + 1}&offset={offset}&{fields}", cancellationToken);
            return new FreePage(books.Take(LibriVoxPage).ToList(), books.Count > LibriVoxPage);
        }

        var since = DateTimeOffset.UtcNow.AddDays(-90).ToUnixTimeSeconds();
        var recent = await ReadLibriVoxAsync(http, $"https://librivox.org/api/feed/audiobooks/?since={since}&format=json&limit=500&{fields}", cancellationToken);
        var newest = recent.OrderByDescending(book => int.TryParse(book.Id, out var number) ? number : 0).ToList();
        return new FreePage(newest.Skip(offset).Take(LibriVoxPage).ToList(), newest.Count > offset + LibriVoxPage);
    }

    public static async Task<FreeDetails?> DetailsAsync(HttpClient http, FreeSource source, string id, CancellationToken cancellationToken) =>
        source == FreeSource.Gutenberg ? await GutenbergDetailsAsync(http, id, cancellationToken) : await LibriVoxDetailsAsync(http, id, cancellationToken);

    // A Gutenberg book's own OPDS entry: its record is a list of "Label: value" paragraphs.
    private static async Task<FreeDetails?> GutenbergDetailsAsync(HttpClient http, string id, CancellationToken cancellationToken)
    {
        if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        var feed = await LoadAsync(http, $"{Gutenberg}/ebooks/{id}.opds", cancellationToken);
        var entry = feed.Root?.Elements(Atom + "entry").FirstOrDefault(item => item.Element(Atom + "content")?.Value.Contains("EBook No.", StringComparison.Ordinal) == true)
            ?? feed.Root?.Elements(Atom + "entry").FirstOrDefault();
        if (entry is null)
        {
            return null;
        }

        var fields = entry.Element(Atom + "content")?.Descendants().Where(element => element.Name.LocalName == "p")
            .Select(paragraph => Regex.Replace(paragraph.Value, @"\s+", " ").Trim())
            .Select(text => (Label: text.Split(':', 2)[0].Trim(), Value: text.Contains(':') ? text.Split(':', 2)[1].Trim() : ""))
            .ToList() ?? [];
        string? Field(string label) => fields.FirstOrDefault(field => field.Label == label).Value is { Length: > 0 } value ? value : null;
        var summary = Field("Summary")?.Replace("(This is an automatically generated summary.)", "").Trim();
        var subjects = fields.Where(field => field.Label == "Subject").Select(field => field.Value.Replace(" -- ", ", ")).Distinct().Take(8).ToList();
        int? downloads = int.TryParse(Field("Downloads"), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : null;
        return new FreeDetails(summary, subjects, Field("Language"), null, Field("Published"), downloads, Field("Reading Level"), null, null,
            $"{Gutenberg}/ebooks/{id}", $"{Gutenberg}/cache/epub/{id}/pg{id}.cover.medium.jpg");
    }

    private static async Task<FreeDetails?> LibriVoxDetailsAsync(HttpClient http, string id, CancellationToken cancellationToken)
    {
        if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        using var response = await http.GetAsync($"https://librivox.org/api/feed/audiobooks/?id={id}&format=json&extended=1", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("books", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
        {
            return null;
        }

        var book = list[0];
        var genres = book.TryGetProperty("genres", out var named) && named.ValueKind == JsonValueKind.Array
            ? named.EnumerateArray().Select(genre => Text(genre, "name")).OfType<string>().ToList()
            : [];
        var translators = book.TryGetProperty("translators", out var people) && people.ValueKind == JsonValueKind.Array
            ? string.Join(", ", people.EnumerateArray().Select(person => $"{Text(person, "first_name")} {Text(person, "last_name")}".Trim()).Where(name => name.Length > 0))
            : "";
        int? sections = int.TryParse(Text(book, "num_sections"), out var parts) ? parts : null;
        return new FreeDetails(Plain(Text(book, "description")), genres, Text(book, "language"), Text(book, "totaltime"),
            Text(book, "copyright_year"), null, null, sections, translators.Length == 0 ? null : translators,
            Text(book, "url_librivox") ?? $"https://librivox.org/search?q={id}", null);
    }

    internal static async Task<(List<FreeBook> Books, bool More)> ReadGutenbergFeedAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        var feed = await LoadAsync(http, url, cancellationToken);
        var books = new List<FreeBook>();
        foreach (var entry in feed.Root?.Elements(Atom + "entry") ?? [])
        {
            var number = GutenbergNumber().Match(entry.Element(Atom + "id")?.Value ?? "");
            var title = entry.Element(Atom + "title")?.Value.Trim();
            if (!number.Success || string.IsNullOrEmpty(title))
            {
                continue;
            }

            var id = number.Groups[1].Value;
            var author = entry.Element(Atom + "content")?.Value.Trim();
            books.Add(new FreeBook(FreeSource.Gutenberg, id, title, string.IsNullOrEmpty(author) ? "Unknown author" : author,
                CoverUrl: $"{Gutenberg}/cache/epub/{id}/pg{id}.cover.small.jpg"));
        }

        var more = feed.Root?.Elements(Atom + "link").Any(link => link.Attribute("rel")?.Value == "next") == true;
        return (books, more);
    }

    private static async Task<XDocument> LoadAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, Async = true });
        return await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
    }

    private static async Task<List<FreeBook>> ReadLibriVoxAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // LibriVox answers an empty page with 404.
            return [];
        }

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("books", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return list.EnumerateArray()
            .Where(book => Text(book, "id") is not null && Text(book, "title") is not null && Text(book, "url_zip_file") is not null)
            .Select(book =>
            {
                var authors = book.TryGetProperty("authors", out var people) && people.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", people.EnumerateArray().Select(person => $"{Text(person, "first_name")} {Text(person, "last_name")}".Trim()).Where(name => name.Length > 0))
                    : "";
                return new FreeBook(FreeSource.LibriVox, Text(book, "id")!, Text(book, "title")!, authors.Length == 0 ? "Unknown author" : authors, Text(book, "language"), Text(book, "totaltime"));
            })
            .ToList();
    }

    // LibriVox descriptions can carry a little HTML; only the words are kept.
    private static string? Plain(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var text = Regex.Replace(html, @"<\s*(br|/p)\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", ""));
        return Regex.Replace(text, @"[ \t]+", " ").Replace("\r", "").Trim();
    }
}

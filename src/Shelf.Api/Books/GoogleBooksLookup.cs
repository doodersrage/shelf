using System.Globalization;
using System.Text.Json;

namespace Shelf.Api.Books;

// Google Books, asked only when Open Library has no match or leaves fields blank: it knows many newer and
// non-English books Open Library does not. Lookup:GoogleBooks=false turns it off, since it sends the ISBN (or the
// title and author) to Google; Lookup:GoogleBooksKey raises Google's daily allowance.
public sealed class GoogleBooksLookup(HttpClient http, IConfiguration configuration)
{
    public bool Enabled => configuration.GetValue("Lookup:GoogleBooks", true);

    public async Task<CatalogMatch?> FindAsync(string? isbn, string? title, string? author, CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return null;
        }

        var normalized = BookRules.NormalizeIsbn(isbn);
        var query = normalized is not null
            ? $"isbn:{normalized}"
            : !string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(author)
                ? $"intitle:\"{title.Trim()}\" inauthor:\"{author.Trim()}\""
                : null;
        if (query is null)
        {
            return null;
        }

        var address = $"books/v1/volumes?q={Uri.EscapeDataString(query)}&maxResults=1&printType=books";
        if (configuration["Lookup:GoogleBooksKey"] is { Length: > 0 } key)
        {
            address += $"&key={Uri.EscapeDataString(key)}";
        }

        try
        {
            using var response = await http.GetAsync(address, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0
                || !items[0].TryGetProperty("volumeInfo", out var volume))
            {
                return null;
            }

            var found = Text(volume, "title");
            var by = volume.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array
                ? string.Join(", ", authors.EnumerateArray().Where(name => name.ValueKind == JsonValueKind.String).Select(name => name.GetString()!.Trim()).Where(name => name.Length > 0))
                : null;
            if (found is null || string.IsNullOrEmpty(by))
            {
                return null;
            }

            var pages = volume.TryGetProperty("pageCount", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var number) && number is >= 1 and <= 20000 ? number : (int?)null;
            var isbn13 = volume.TryGetProperty("industryIdentifiers", out var identifiers) && identifiers.ValueKind == JsonValueKind.Array
                ? identifiers.EnumerateArray().Where(item => Text(item, "type") is "ISBN_13" or "ISBN_10").Select(item => BookRules.NormalizeIsbn(Text(item, "identifier"))).FirstOrDefault(value => value is not null)
                : null;
            var categories = volume.TryGetProperty("categories", out var listed) && listed.ValueKind == JsonValueKind.Array
                ? listed.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToList()
                : [];
            return new CatalogMatch(
                found,
                by.Length > 200 ? by[..200] : by,
                BookRules.ParsePublishYear(Text(volume, "publishedDate")),
                pages,
                Text(volume, "publisher"),
                Language(Text(volume, "language")),
                Cover(volume),
                normalized ?? isbn13,
                BookRules.CleanSubtitle(Text(volume, "subtitle")),
                null,
                BookRules.UsefulSubjects(categories));
        }
        catch (Exception ex) when (ex is HttpRequestException or Polly.ExecutionRejectedException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    // Google's thumbnail, over https and without the page-curl it draws on by default.
    private static string? Cover(JsonElement volume)
    {
        if (!volume.TryGetProperty("imageLinks", out var links))
        {
            return null;
        }

        var link = Text(links, "thumbnail") ?? Text(links, "smallThumbnail");
        return link?.Replace("http://", "https://", StringComparison.Ordinal).Replace("&edge=curl", "", StringComparison.Ordinal);
    }

    // Google gives a language code such as en; the catalog keeps its English name.
    private static string? Language(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(code);
            return (culture.IsNeutralCulture ? culture : culture.Parent).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text
            ? text.Trim()
            : null;
}

// Open Library first, as before; Google Books for a book it does not know, and for the fields it leaves blank.
public sealed class CatalogLookup(OpenLibraryLookup openLibrary, GoogleBooksLookup google) : IBookLookup
{
    public async Task<CatalogMatch?> FindAsync(string? isbn, string? title, string? author, CancellationToken cancellationToken)
    {
        var first = await openLibrary.FindAsync(isbn, title, author, cancellationToken);
        if (first is not null && !Lacking(first))
        {
            return first;
        }

        var second = await google.FindAsync(isbn ?? first?.Isbn, title ?? first?.Title, author ?? first?.Author, cancellationToken);
        if (first is null || second is null)
        {
            return first ?? second;
        }

        return first with
        {
            Year = first.Year ?? second.Year,
            Pages = first.Pages ?? second.Pages,
            Publisher = first.Publisher ?? second.Publisher,
            Language = first.Language ?? second.Language,
            CoverUrl = first.CoverUrl ?? second.CoverUrl,
            Isbn = first.Isbn ?? second.Isbn,
            Subtitle = first.Subtitle ?? second.Subtitle,
            Tags = first.Tags is { Length: > 0 } ? first.Tags : second.Tags,
        };
    }

    private static bool Lacking(CatalogMatch match) =>
        match.Pages is null || match.Publisher is null || match.CoverUrl is null || match.Year is null;
}

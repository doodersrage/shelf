using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shelf.Api.Books;

public interface IBookLookup
{
    Task<CatalogMatch?> FindAsync(string isbn, CancellationToken cancellationToken);
}

public sealed class OpenLibraryLookup(HttpClient http) : IBookLookup
{
    public async Task<CatalogMatch?> FindAsync(string isbn, CancellationToken cancellationToken)
    {
        var normalized = BookRules.NormalizeIsbn(isbn);
        if (normalized is null)
        {
            return null;
        }

        try
        {
            var response = await http.GetAsync(
                $"search.json?isbn={Uri.EscapeDataString(normalized)}&fields=title,author_name,first_publish_year,number_of_pages_median,publisher,cover_i,language&limit=1",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<OpenLibrarySearch>(cancellationToken);
            var doc = payload?.Docs?.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(doc?.Title))
            {
                return null;
            }

            var author = doc.AuthorName?.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
            if (string.IsNullOrWhiteSpace(author))
            {
                return null;
            }

            var pages = doc.Pages is { } count ? (int)Math.Round(count) : (int?)null;
            return new CatalogMatch(
                doc.Title.Trim(),
                author.Trim(),
                doc.FirstPublishYear is >= 1 and <= 3000 ? doc.FirstPublishYear : null,
                pages is >= 1 and <= 20000 ? pages : null,
                First(doc.Publisher),
                LanguageName(doc.Language?.FirstOrDefault()),
                doc.CoverId is int cover and > 0 ? $"https://covers.openlibrary.org/b/id/{cover}-M.jpg" : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static string? First(IEnumerable<string>? values) =>
        values?.Select(value => value.Trim()).FirstOrDefault(value => value.Length > 0);

    private static string? LanguageName(string? code) => code?.ToLowerInvariant() switch
    {
        null or "" => null,
        "eng" => "English",
        "fre" or "fra" => "French",
        "spa" => "Spanish",
        "ger" or "deu" => "German",
        "ita" => "Italian",
        "por" => "Portuguese",
        "rus" => "Russian",
        "jpn" => "Japanese",
        "chi" or "zho" => "Chinese",
        "kor" => "Korean",
        "lat" => "Latin",
        "ara" => "Arabic",
        "swe" => "Swedish",
        "dut" or "nld" => "Dutch",
        "pol" => "Polish",
        "hun" => "Hungarian",
        "cze" or "ces" => "Czech",
        "fin" => "Finnish",
        "nor" => "Norwegian",
        "dan" => "Danish",
        "gre" or "ell" => "Greek",
        "heb" => "Hebrew",
        _ => null,
    };

    private sealed class OpenLibrarySearch
    {
        public OpenLibraryDoc[]? Docs { get; set; }
    }

    private sealed class OpenLibraryDoc
    {
        public string? Title { get; set; }

        [JsonPropertyName("author_name")]
        public string[]? AuthorName { get; set; }

        [JsonPropertyName("first_publish_year")]
        public int? FirstPublishYear { get; set; }

        [JsonPropertyName("number_of_pages_median")]
        public double? Pages { get; set; }

        public string[]? Publisher { get; set; }

        [JsonPropertyName("cover_i")]
        public int? CoverId { get; set; }

        public string[]? Language { get; set; }
    }
}

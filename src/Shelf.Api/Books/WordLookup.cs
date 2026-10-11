using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

public sealed record WordSense(string PartOfSpeech, string Definition);

public sealed record WordMeaning(string Word, string Language, WordSense[] Senses, string? DictionaryUrl, string? Summary, string? SummaryTitle, string? EncyclopediaUrl);

public interface IWordLookup
{
    Task<WordMeaning?> FindAsync(string words, string language, CancellationToken cancellationToken);
}

// Words chosen in the reader, looked up in Wiktionary for a definition and Wikipedia for a summary, in the book's
// language. Only the words go out, to Wikimedia; Lookup:Dictionary false turns it off.
public sealed partial class WikimediaLookup(HttpClient http) : IWordLookup
{
    public async Task<WordMeaning?> FindAsync(string words, string language, CancellationToken cancellationToken)
    {
        var title = Uri.EscapeDataString(words.Replace(' ', '_'));
        var senses = new List<WordSense>();
        string? dictionary = null;
        try
        {
            // The English Wiktionary's definitions come keyed by the language of each entry.
            var answer = await http.GetAsync($"https://en.wiktionary.org/api/rest_v1/page/definition/{title}", cancellationToken);
            if (answer.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(cancellationToken));
                var entries = document.RootElement.TryGetProperty(language, out var wanted) ? wanted
                    : document.RootElement.TryGetProperty("en", out var english) ? english : default;
                if (entries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in entries.EnumerateArray())
                    {
                        var part = entry.TryGetProperty("partOfSpeech", out var named) ? named.GetString() ?? "" : "";
                        if (!entry.TryGetProperty("definitions", out var list) || list.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        foreach (var definition in list.EnumerateArray())
                        {
                            var text = Plain(definition.TryGetProperty("definition", out var said) ? said.GetString() : null);
                            if (text.Length > 0 && senses.Count < 6)
                            {
                                senses.Add(new WordSense(part, text));
                            }
                        }
                    }
                }

                dictionary = senses.Count > 0 ? $"https://en.wiktionary.org/wiki/{title}" : null;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or Polly.ExecutionRejectedException)
        {
        }

        string? summary = null, summaryTitle = null, encyclopedia = null;
        try
        {
            var answer = await http.GetAsync($"https://{language}.wikipedia.org/api/rest_v1/page/summary/{title}", cancellationToken);
            if (answer.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(cancellationToken));
                var root = document.RootElement;
                if (root.TryGetProperty("type", out var type) && type.GetString() == "standard" && root.TryGetProperty("extract", out var extract))
                {
                    summary = extract.GetString();
                    summaryTitle = root.TryGetProperty("title", out var named) ? named.GetString() : words;
                    encyclopedia = root.TryGetProperty("content_urls", out var urls) && urls.TryGetProperty("desktop", out var desktop) && desktop.TryGetProperty("page", out var page) ? page.GetString() : null;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or Polly.ExecutionRejectedException)
        {
        }

        return senses.Count == 0 && summary is null ? null : new WordMeaning(words, language, senses.ToArray(), dictionary, summary, summaryTitle, encyclopedia);
    }

    private static string Plain(string? html) => WebUtility.HtmlDecode(Tags().Replace(html ?? "", "")).Trim();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}

public static partial class WordLookups
{
    public const int MaxLength = 60;

    public static void Map(RouteGroupBuilder books) =>
        books.MapGet("/{id:int}/look-up", LookUp);

    // The book's language as a two-letter code, for the dictionary and the encyclopedia; English when it has none.
    public static string LanguageOf(string? language)
    {
        var name = (language ?? "").Trim().ToLowerInvariant();
        if (name.Length == 2 && name.All(char.IsAsciiLetterLower))
        {
            return name;
        }

        return name switch
        {
            "french" or "français" => "fr",
            "german" or "deutsch" => "de",
            "spanish" or "español" => "es",
            "italian" or "italiano" => "it",
            "portuguese" or "português" => "pt",
            "dutch" or "nederlands" => "nl",
            "swedish" => "sv",
            "danish" => "da",
            "norwegian" => "no",
            "finnish" => "fi",
            "polish" => "pl",
            "russian" => "ru",
            "latin" => "la",
            "greek" => "el",
            "japanese" => "ja",
            "chinese" => "zh",
            _ => "en",
        };
    }

    // Words worth looking up: a word or a short phrase, cleaned of the punctuation around it.
    public static string? Clean(string? words)
    {
        var text = Edges().Replace(Spaces().Replace(words ?? "", " "), "").Trim();
        return text.Length is > 0 and <= MaxLength && text.Count(character => character == ' ') <= 3 ? text : null;
    }

    private static async Task<IResult> LookUp(int id, string? words, ShelfDb db, IWordLookup lookup, IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Lookup:Dictionary", true))
        {
            return TypedResults.NotFound();
        }

        if (await Lending.OpenAsync(db, id, cancellationToken) is not { } open || Clean(words) is not { } wanted)
        {
            return TypedResults.NotFound();
        }

        return await lookup.FindAsync(wanted, LanguageOf(open.Book.Language), cancellationToken) is { } meaning
            ? TypedResults.Ok(meaning)
            : TypedResults.NotFound();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^[^\p{L}\p{N}]+|[^\p{L}\p{N}]+$")]
    private static partial Regex Edges();
}

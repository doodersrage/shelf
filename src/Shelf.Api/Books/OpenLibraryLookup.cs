using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shelf.Api.Books;

public interface IBookLookup
{
    Task<CatalogMatch?> FindAsync(string? isbn, string? title, string? author, CancellationToken cancellationToken);
}

public sealed class OpenLibraryLookup(HttpClient http) : IBookLookup
{
    public async Task<CatalogMatch?> FindAsync(string? isbn, string? title, string? author, CancellationToken cancellationToken)
    {
        try
        {
            var normalized = BookRules.NormalizeIsbn(isbn);
            if (normalized is not null)
            {
                return await FindIsbnAsync(normalized, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(author))
            {
                return await FindTitleAsync(title.Trim(), author.Trim(), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or Polly.ExecutionRejectedException or TaskCanceledException or JsonException)
        {
            return null;
        }

        return null;
    }

    private async Task<CatalogMatch?> FindIsbnAsync(string isbn, CancellationToken cancellationToken)
    {
        var editionTask = ReadAsync<OpenLibraryEdition>($"isbn/{Uri.EscapeDataString(isbn)}.json", cancellationToken);
        var searchTask = ReadAsync<OpenLibrarySearch>(
            $"search.json?isbn={Uri.EscapeDataString(isbn)}&fields=title,author_name,first_publish_year,number_of_pages_median,cover_i,subject&limit=1",
            cancellationToken);
        await Task.WhenAll(editionTask, searchTask);

        var edition = await editionTask;
        var doc = (await searchTask)?.Docs?.FirstOrDefault();
        if (edition is null && string.IsNullOrWhiteSpace(doc?.Title))
        {
            return null;
        }

        var title = FirstText(edition?.Title, doc?.Title);
        var author = await AuthorAsync(doc, edition, cancellationToken);
        if (title is null || author is null)
        {
            return null;
        }

        var pages = edition?.NumberOfPages is >= 1 and <= 20000
            ? edition.NumberOfPages
            : MedianPages(doc?.Pages);
        return new CatalogMatch(
            title,
            author,
            doc?.FirstPublishYear is >= 1000 and <= 2100 ? doc.FirstPublishYear : BookRules.ParsePublishYear(edition?.PublishDate),
            pages,
            First(edition?.Publishers),
            LanguageName(LanguageCode(edition?.Languages) ?? PreferredLanguage(doc?.Language)),
            Cover(edition?.Covers?.FirstOrDefault(id => id > 0) ?? doc?.CoverId),
            isbn,
            BookRules.CleanSubtitle(edition?.Subtitle),
            BookRules.ParseFormat(edition?.PhysicalFormat),
            BookRules.UsefulSubjects(doc?.Subject));
    }

    private async Task<CatalogMatch?> FindTitleAsync(string title, string author, CancellationToken cancellationToken)
    {
        var search = await ReadAsync<OpenLibrarySearch>(
            "search.json?title=" + Uri.EscapeDataString(title)
            + "&author=" + Uri.EscapeDataString(author)
            + "&fields=key,title,author_name,first_publish_year,number_of_pages_median,cover_i,subject&limit=1",
            cancellationToken);
        var doc = search?.Docs?.FirstOrDefault();
        var workTitle = Blank(doc?.Title);
        var workAuthor = doc?.AuthorName?.Select(Blank).FirstOrDefault(name => name is not null);
        if (workTitle is null || workAuthor is null)
        {
            return null;
        }

        var typicalPages = MedianPages(doc?.Pages);
        var chosen = await ChooseAsync(doc?.Key, workTitle, typicalPages, cancellationToken);
        return new CatalogMatch(
            workTitle,
            workAuthor,
            doc?.FirstPublishYear is >= 1000 and <= 2100 ? doc.FirstPublishYear : chosen?.Year,
            chosen?.Pages is >= 1 and <= 20000 ? chosen.Pages : typicalPages,
            Blank(chosen?.Publisher),
            LanguageName(chosen?.Language),
            Cover(chosen?.CoverId ?? doc?.CoverId),
            BookRules.NormalizeIsbn(chosen?.Isbn),
            BookRules.CleanSubtitle(chosen?.Subtitle),
            BookRules.ParseFormat(chosen?.PhysicalFormat),
            BookRules.UsefulSubjects(doc?.Subject));
    }

    private async Task<EditionChoice?> ChooseAsync(string? workKey, string workTitle, int? typicalPages, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workKey))
        {
            return null;
        }

        var key = workKey.Trim().TrimStart('/');
        OpenLibraryEditions? list;
        try
        {
            list = await ReadAsync<OpenLibraryEditions>($"{key}/editions.json?limit=100", cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or Polly.ExecutionRejectedException or TaskCanceledException or JsonException)
        {
            return null;
        }

        var editions = (list?.Entries ?? []).Select(ToChoice);
        return BookRules.ChooseEdition(editions, workTitle, typicalPages);
    }

    private async Task<string?> AuthorAsync(OpenLibraryDoc? doc, OpenLibraryEdition? edition, CancellationToken cancellationToken)
    {
        var named = doc?.AuthorName?.Select(Blank).FirstOrDefault(name => name is not null);
        if (named is not null)
        {
            return named;
        }

        var key = edition?.Authors?.Select(author => author.Key).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (key is null)
        {
            return null;
        }

        var author = await ReadAsync<OpenLibraryAuthor>(key.Trim().TrimStart('/') + ".json", cancellationToken);
        return Blank(author?.Name);
    }

    private async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        var response = await http.GetAsync(path, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return default;
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    private static EditionChoice ToChoice(OpenLibraryEdition edition) => new(
        edition.Title,
        First(edition.Publishers),
        BookRules.ParsePublishYear(edition.PublishDate),
        edition.NumberOfPages is >= 1 and <= 20000 ? edition.NumberOfPages : null,
        LanguageCode(edition.Languages),
        edition.PhysicalFormat,
        edition.Covers?.FirstOrDefault(id => id > 0),
        edition.Subtitle,
        First(edition.Isbn13) ?? First(edition.Isbn10));

    private static int? MedianPages(double? pages)
    {
        if (pages is not { } count)
        {
            return null;
        }

        var rounded = (int)Math.Round(count);
        return rounded is >= 1 and <= 20000 ? rounded : null;
    }

    private static string? Cover(int? id) =>
        id is > 0 ? $"https://covers.openlibrary.org/b/id/{id}-M.jpg" : null;

    private static string? LanguageCode(IEnumerable<OpenLibraryRef>? languages) =>
        languages?
            .Select(language => language.Key?.Split('/').LastOrDefault())
            .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code));

    private static string? PreferredLanguage(IEnumerable<string>? codes)
    {
        var list = codes?.Where(code => !string.IsNullOrWhiteSpace(code)).Select(code => code.ToLowerInvariant()).ToList();
        if (list is null || list.Count == 0)
        {
            return null;
        }

        return list.Contains("eng") ? "eng" : list.Count == 1 ? list[0] : null;
    }

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

    private static string? First(IEnumerable<string>? values) =>
        values?.Select(Blank).FirstOrDefault(value => value is not null);

    private static string? FirstText(string? left, string? right) => Blank(left) ?? Blank(right);

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class OpenLibrarySearch
    {
        public OpenLibraryDoc[]? Docs { get; set; }
    }

    private sealed class OpenLibraryDoc
    {
        public string? Key { get; set; }

        public string? Title { get; set; }

        [JsonPropertyName("author_name")]
        public string[]? AuthorName { get; set; }

        [JsonPropertyName("first_publish_year")]
        public int? FirstPublishYear { get; set; }

        [JsonPropertyName("number_of_pages_median")]
        public double? Pages { get; set; }

        [JsonPropertyName("cover_i")]
        public int? CoverId { get; set; }

        public string[]? Language { get; set; }

        public string[]? Subject { get; set; }
    }

    private sealed class OpenLibraryEditions
    {
        public OpenLibraryEdition[]? Entries { get; set; }
    }

    private sealed class OpenLibraryEdition
    {
        public string? Title { get; set; }

        public string? Subtitle { get; set; }

        public string[]? Publishers { get; set; }

        [JsonPropertyName("publish_date")]
        public string? PublishDate { get; set; }

        [JsonPropertyName("number_of_pages")]
        public int? NumberOfPages { get; set; }

        public OpenLibraryRef[]? Languages { get; set; }

        [JsonPropertyName("physical_format")]
        public string? PhysicalFormat { get; set; }

        public int[]? Covers { get; set; }

        [JsonPropertyName("isbn_13")]
        public string[]? Isbn13 { get; set; }

        [JsonPropertyName("isbn_10")]
        public string[]? Isbn10 { get; set; }

        public OpenLibraryRef[]? Authors { get; set; }
    }

    private sealed class OpenLibraryRef
    {
        public string? Key { get; set; }
    }

    private sealed class OpenLibraryAuthor
    {
        public string? Name { get; set; }
    }
}

using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// One audiobook in Audible's catalogue, as a match for a recording on the shelf.
public sealed record AudibleMatch(
    string Asin,
    string Title,
    string? Subtitle,
    string Author,
    string? Narrator,
    string? Series,
    string? SeriesPosition,
    string? Publisher,
    int? Year,
    string? Language,
    int? Minutes,
    string? Cover);

// A chapter as Audible marks it: from the start of the whole recording.
public sealed record AudibleChapter(string Title, double Start);

// What to take from a match: the details (narrator, series, publisher, and so on), the chapter names, the cover.
public sealed record AudibleApplyRequest(string Asin, string? Region = null, bool Details = true, bool Chapters = true, bool Cover = false);

public sealed record AudibleApplied(bool Details, int Chapters, bool Cover);

public interface IAudibleCatalog
{
    Task<IReadOnlyList<AudibleMatch>> SearchAsync(string title, string? author, string region, CancellationToken cancellationToken);

    Task<AudibleMatch?> FindAsync(string asin, string region, CancellationToken cancellationToken);

    Task<IReadOnlyList<AudibleChapter>> ChaptersAsync(string asin, string region, CancellationToken cancellationToken);

    Task<byte[]?> ImageAsync(string url, CancellationToken cancellationToken);
}

// Audible's own catalogue for the search and details, which needs no account, and Audnexus for the chapters,
// which Audible gives only to signed-in apps. Both are asked by region, as each store sells its own editions.
public sealed partial class AudibleCatalog(HttpClient http) : IAudibleCatalog
{
    public static readonly IReadOnlyDictionary<string, string> Regions = new Dictionary<string, string>
    {
        ["us"] = ".com",
        ["ca"] = ".ca",
        ["uk"] = ".co.uk",
        ["au"] = ".com.au",
        ["fr"] = ".fr",
        ["de"] = ".de",
        ["jp"] = ".co.jp",
        ["it"] = ".it",
        ["in"] = ".in",
        ["es"] = ".es",
    };

    private const string Groups = "contributors,product_desc,product_attrs,product_extended_attrs,media,series";

    public static string Region(string? region) => region is not null && Regions.ContainsKey(region.ToLowerInvariant()) ? region.ToLowerInvariant() : "us";

    public async Task<IReadOnlyList<AudibleMatch>> SearchAsync(string title, string? author, string region, CancellationToken cancellationToken)
    {
        var query = $"num_results=10&products_sort_by=Relevance&response_groups={Groups}&image_sizes=500&title={Uri.EscapeDataString(title)}"
            + (string.IsNullOrWhiteSpace(author) ? "" : $"&author={Uri.EscapeDataString(author)}");
        var found = await GetAsync($"https://api.audible{Regions[Region(region)]}/1.0/catalog/products?{query}", cancellationToken);
        return found is { } data && data.TryGetProperty("products", out var products) && products.ValueKind == JsonValueKind.Array
            ? products.EnumerateArray().Select(Read).OfType<AudibleMatch>().ToList()
            : [];
    }

    public async Task<AudibleMatch?> FindAsync(string asin, string region, CancellationToken cancellationToken)
    {
        if (!IsAsin(asin))
        {
            return null;
        }

        var found = await GetAsync($"https://api.audible{Regions[Region(region)]}/1.0/catalog/products/{asin}?response_groups={Groups}&image_sizes=500", cancellationToken);
        return found is { } data && data.TryGetProperty("product", out var product) ? Read(product) : null;
    }

    public async Task<IReadOnlyList<AudibleChapter>> ChaptersAsync(string asin, string region, CancellationToken cancellationToken)
    {
        if (!IsAsin(asin))
        {
            return [];
        }

        var found = await GetAsync($"https://api.audnex.us/books/{asin}/chapters?region={Region(region)}", cancellationToken);
        if (found is not { } data || !data.TryGetProperty("chapters", out var chapters) || chapters.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return chapters.EnumerateArray()
            .Select(chapter => new AudibleChapter(
                chapter.TryGetProperty("title", out var name) ? name.GetString()?.Trim() ?? "" : "",
                chapter.TryGetProperty("startOffsetMs", out var start) && start.TryGetInt64(out var ms) ? ms / 1000.0 : -1))
            .Where(chapter => chapter.Start >= 0)
            .ToList();
    }

    // Covers come only from Amazon's picture servers, so a match cannot send the shelf anywhere else.
    public async Task<byte[]?> ImageAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps || !address.Host.EndsWith(".media-amazon.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var answer = await http.GetAsync(address, cancellationToken);
            return answer.IsSuccessStatusCode && answer.Content.Headers.ContentLength is null or <= CoverStore.MaxBytes ? await answer.Content.ReadAsByteArrayAsync(cancellationToken) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    public static bool IsAsin(string? asin) => asin is not null && AsinPattern().IsMatch(asin);

    // One product from the catalogue; null when it is not an audiobook with a title.
    public static AudibleMatch? Read(JsonElement product)
    {
        if (!product.TryGetProperty("asin", out var asin) || asin.GetString() is not { } id || !IsAsin(id) || Text(product, "title") is not { } title)
        {
            return null;
        }

        var series = product.TryGetProperty("series", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0 ? list[0] : default;
        var cover = product.TryGetProperty("product_images", out var images) && images.ValueKind == JsonValueKind.Object
            ? images.EnumerateObject().Select(image => image.Value.GetString()).FirstOrDefault(url => url is not null)
            : null;
        var released = Text(product, "release_date") ?? Text(product, "issue_date");
        return new AudibleMatch(
            id,
            title,
            Text(product, "subtitle"),
            Names(product, "authors") ?? "",
            Names(product, "narrators"),
            series.ValueKind == JsonValueKind.Object ? Text(series, "title") : null,
            series.ValueKind == JsonValueKind.Object ? Text(series, "sequence") : null,
            Text(product, "publisher_name"),
            released is { Length: >= 4 } && int.TryParse(released[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : null,
            Text(product, "language") is { } language ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(language) : null,
            product.TryGetProperty("runtime_length_min", out var minutes) && minutes.TryGetInt32(out var runtime) ? runtime : null,
            cover);
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text.Trim() : null;

    private static string? Names(JsonElement item, string name) =>
        item.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? string.Join(", ", list.EnumerateArray().Select(person => Text(person, "name")).OfType<string>()) is { Length: > 0 } names ? names : null
            : null;

    private async Task<JsonElement?> GetAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var answer = await http.GetAsync(url, cancellationToken);
            return answer.IsSuccessStatusCode ? (await answer.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).Clone() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex("^[A-Z0-9]{10}$")]
    private static partial Regex AsinPattern();
}

public static class Audible
{
    public static void Map(RouteGroupBuilder books)
    {
        books.MapGet("/{id:int}/audible", Search);
        books.MapPost("/{id:int}/audible", Apply);
    }

    private static async Task<Results<Ok<IReadOnlyList<AudibleMatch>>, NotFound>> Search(int id, string? title, string? author, string? region, ShelfDb db, IAudibleCatalog catalog, CancellationToken cancellationToken)
    {
        if (await db.Books.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken) is not { } book)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(await SearchAsync(catalog, string.IsNullOrWhiteSpace(title) ? book.Title : title, author ?? book.Author, region, cancellationToken));
    }

    // An ASIN typed in is looked up as it is; anything else is searched by title and author.
    public static async Task<IReadOnlyList<AudibleMatch>> SearchAsync(IAudibleCatalog catalog, string title, string? author, string? region, CancellationToken cancellationToken = default)
    {
        title = title.Trim();
        if (AudibleCatalog.IsAsin(title.ToUpperInvariant()))
        {
            return await catalog.FindAsync(title.ToUpperInvariant(), AudibleCatalog.Region(region), cancellationToken) is { } one ? [one] : [];
        }

        return title.Length == 0 ? [] : await catalog.SearchAsync(title, string.IsNullOrWhiteSpace(author) ? null : author.Trim(), AudibleCatalog.Region(region), cancellationToken);
    }

    private static async Task<Results<Ok<AudibleApplied>, NotFound, ValidationProblem>> Apply(int id, AudibleApplyRequest request, ShelfDb db, IAudibleCatalog catalog, CoverStore covers, CancellationToken cancellationToken)
    {
        if (!AudibleCatalog.IsAsin(request.Asin))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Asin"] = [T("Give an Audible ASIN: ten letters and digits.")] });
        }

        return await ApplyAsync(db, catalog, covers, id, request, cancellationToken) is { } applied ? TypedResults.Ok(applied) : TypedResults.NotFound();
    }

    // Takes what was asked from a match onto one of the reader's own books. Null when the book or the match is not there.
    public static async Task<AudibleApplied?> ApplyAsync(ShelfDb db, IAudibleCatalog catalog, CoverStore covers, int id, AudibleApplyRequest request, CancellationToken cancellationToken = default)
    {
        var region = AudibleCatalog.Region(request.Region);
        if (await db.Books.FirstOrDefaultAsync(item => item.Id == id, cancellationToken) is not { } book
            || await catalog.FindAsync(request.Asin, region, cancellationToken) is not { } match)
        {
            return null;
        }

        book.Asin = match.Asin;
        if (request.Details)
        {
            book.Narrator = Fit(match.Narrator, 200) ?? book.Narrator;
            book.Subtitle = Fit(match.Subtitle, 200) ?? book.Subtitle;
            book.Publisher = Fit(match.Publisher, 200) ?? book.Publisher;
            book.Year = match.Year ?? book.Year;
            book.Language = Fit(match.Language, 40) ?? book.Language;
            if (Fit(match.Series, 200) is { } series)
            {
                book.Series = series;
                book.SeriesNumber = WholeNumber(match.SeriesPosition) ?? book.SeriesNumber;
            }

            book.Format ??= BookFormat.Audiobook;
        }

        var chapters = 0;
        if (request.Chapters)
        {
            var marks = await catalog.ChaptersAsync(match.Asin, region, cancellationToken);
            if (marks.Count > 1)
            {
                book.AudioChapterNames = JsonSerializer.Serialize(marks.Select(mark => new AudibleChapter(Fit(mark.Title, 200) ?? "", Math.Round(mark.Start, 3))));
                chapters = marks.Count;
            }
        }

        var cover = false;
        if (request.Cover && match.Cover is { } url && await catalog.ImageAsync(url, cancellationToken) is { } bytes && await covers.SaveAsync(bytes, cancellationToken) is { } stored)
        {
            covers.Delete(book.CoverImage);
            (book.CoverImage, book.CoverUrl, cover) = (stored, null, true);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new AudibleApplied(request.Details, chapters, cover);
    }

    public static async Task ForgetChaptersAsync(ShelfDb db, int id, CancellationToken cancellationToken = default) =>
        await db.Books.Where(item => item.Id == id).ExecuteUpdateAsync(set => set.SetProperty(item => item.AudioChapterNames, (string?)null), cancellationToken);

    // Audible's chapters, from the start of the whole recording, put into the tracks they fall in. Null when the shelf
    // keeps none, a track's length is unknown, or they run past the end, as they would for another edition.
    public static AudioPlanChapter[]? Place(string? json, IReadOnlyList<double?> lengths)
    {
        if (string.IsNullOrWhiteSpace(json) || lengths.Count == 0 || lengths.Any(length => length is null))
        {
            return null;
        }

        List<AudibleChapter>? marks;
        try
        {
            marks = JsonSerializer.Deserialize<List<AudibleChapter>>(json);
        }
        catch (JsonException)
        {
            return null;
        }

        var total = lengths.Sum(length => length!.Value);
        if (marks is not { Count: > 1 } || marks[^1].Start >= total)
        {
            return null;
        }

        var placed = new List<AudioPlanChapter>();
        foreach (var (mark, index) in marks.Select((mark, index) => (mark, index)))
        {
            var (track, before) = (0, 0.0);
            while (track < lengths.Count - 1 && mark.Start >= before + lengths[track]!.Value)
            {
                before += lengths[track]!.Value;
                track++;
            }

            placed.Add(new AudioPlanChapter(index, mark.Title.Length > 0 ? mark.Title : T("Chapter {0}", index + 1), track, Math.Round(mark.Start - before, 3)));
        }

        return [.. placed];
    }

    private static int? WholeNumber(string? position) =>
        double.TryParse(position, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number >= 0 && number < 100_000 && number == Math.Floor(number) ? (int)number : null;

    private static string? Fit(string? text, int most) => string.IsNullOrWhiteSpace(text) ? null : text.Trim() is var trimmed && trimmed.Length > most ? trimmed[..most] : text.Trim();
}

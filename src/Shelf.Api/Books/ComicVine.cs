using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

// One comic issue in ComicVine's catalogue.
public sealed record ComicIssue(int Id, string Series, string? Number, string? Name, int? Year, string? Cover, int? VolumeId);

public sealed record ComicIssueDetails(ComicIssue Issue, string? Writers, string? Publisher);

public interface IComicCatalog
{
    bool Ready { get; }

    Task<IReadOnlyList<ComicIssue>> SearchAsync(string query, CancellationToken cancellationToken);

    Task<ComicIssueDetails?> IssueAsync(int id, CancellationToken cancellationToken);

    Task<byte[]?> ImageAsync(string url, CancellationToken cancellationToken);
}

// ComicVine, with the API key an admin gives (Comics:ComicVineKey, free from comicvine.gamespot.com/api).
public sealed class ComicVineCatalog(HttpClient http, IConfiguration configuration) : IComicCatalog
{
    private const string Site = "https://comicvine.gamespot.com/api";

    public bool Ready => !string.IsNullOrWhiteSpace(configuration["Comics:ComicVineKey"]);

    private string Key => Uri.EscapeDataString(configuration["Comics:ComicVineKey"] ?? "");

    public async Task<IReadOnlyList<ComicIssue>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var found = await GetAsync($"{Site}/search/?api_key={Key}&format=json&resources=issue&limit=12&field_list=id,name,issue_number,volume,cover_date,image&query={Uri.EscapeDataString(query)}", cancellationToken);
        return found is { } data && data.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
            ? results.EnumerateArray().Select(Read).OfType<ComicIssue>().ToList()
            : [];
    }

    public async Task<ComicIssueDetails?> IssueAsync(int id, CancellationToken cancellationToken)
    {
        var found = await GetAsync($"{Site}/issue/4000-{id}/?api_key={Key}&format=json&field_list=id,name,issue_number,volume,cover_date,image,person_credits", cancellationToken);
        if (found is not { } data || !data.TryGetProperty("results", out var result) || Read(result) is not { } issue)
        {
            return null;
        }

        var writers = result.TryGetProperty("person_credits", out var people) && people.ValueKind == JsonValueKind.Array
            ? string.Join(", ", people.EnumerateArray()
                .Where(person => Text(person, "role")?.Split(',').Any(role => role.Trim().Equals("writer", StringComparison.OrdinalIgnoreCase)) == true)
                .Select(person => Text(person, "name")).OfType<string>())
            : "";
        string? publisher = null;
        if (issue.VolumeId is int volume && await GetAsync($"{Site}/volume/4050-{volume}/?api_key={Key}&format=json&field_list=publisher", cancellationToken) is { } volumeData
            && volumeData.TryGetProperty("results", out var volumeResult) && volumeResult.TryGetProperty("publisher", out var house) && house.ValueKind == JsonValueKind.Object)
        {
            publisher = Text(house, "name");
        }

        return new ComicIssueDetails(issue, writers.Length > 0 ? writers : null, publisher);
    }

    // Covers come only from ComicVine's own servers.
    public async Task<byte[]?> ImageAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps || !address.Host.EndsWith("comicvine.gamespot.com", StringComparison.OrdinalIgnoreCase))
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

    public static ComicIssue? Read(JsonElement item)
    {
        if (!item.TryGetProperty("id", out var id) || !id.TryGetInt32(out var number))
        {
            return null;
        }

        var volume = item.TryGetProperty("volume", out var found) && found.ValueKind == JsonValueKind.Object ? found : default;
        var date = Text(item, "cover_date");
        var image = item.TryGetProperty("image", out var images) && images.ValueKind == JsonValueKind.Object ? Text(images, "original_url") ?? Text(images, "super_url") ?? Text(images, "medium_url") : null;
        return new ComicIssue(
            number,
            volume.ValueKind == JsonValueKind.Object ? Text(volume, "name") ?? "" : "",
            Text(item, "issue_number"),
            Text(item, "name"),
            date is { Length: >= 4 } && int.TryParse(date[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : null,
            image,
            volume.ValueKind == JsonValueKind.Object && volume.TryGetProperty("id", out var volumeId) && volumeId.TryGetInt32(out var volumeNumber) ? volumeNumber : null);
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text.Trim() : null;

    private async Task<JsonElement?> GetAsync(string url, CancellationToken cancellationToken)
    {
        if (!Ready)
        {
            return null;
        }

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
}

public static class ComicVine
{
    // Takes an issue's series, number, year, publisher, and writers onto a comic of the reader's, and its cover if asked.
    public static async Task<bool> ApplyAsync(ShelfDb db, IComicCatalog catalog, CoverStore covers, int bookId, int issueId, bool cover, CancellationToken cancellationToken = default)
    {
        var book = await db.Books.FirstOrDefaultAsync(item => item.Id == bookId, cancellationToken);
        if (book is null || await catalog.IssueAsync(issueId, cancellationToken) is not { } details)
        {
            return false;
        }

        var issue = details.Issue;
        if (issue.Series.Length > 0)
        {
            book.Series = issue.Series.Length > 200 ? issue.Series[..200] : issue.Series;
            book.SeriesNumber = double.TryParse(issue.Number, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 999 && number == Math.Floor(number) ? (int)number : book.SeriesNumber;
        }

        book.Author = details.Writers is { } writers ? (writers.Length > 200 ? writers[..200] : writers) : book.Author;
        book.Publisher = details.Publisher ?? book.Publisher;
        book.Year = issue.Year ?? book.Year;
        book.Subtitle = issue.Name is { Length: <= 200 } name ? name : book.Subtitle;
        if (cover && issue.Cover is { } url && await catalog.ImageAsync(url, cancellationToken) is { } bytes && await covers.SaveAsync(bytes, cancellationToken) is { } stored)
        {
            covers.Delete(book.CoverImage);
            (book.CoverImage, book.CoverUrl) = (stored, null);
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

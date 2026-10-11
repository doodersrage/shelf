using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Shelf.Api.Books;

// Where a book can be had for nothing: lent by Open Library, free from Project Gutenberg, read aloud by LibriVox.
public sealed record BookAvailability(string? OpenLibraryUrl, bool OpenLibraryFree, FreeBook? Gutenberg, FreeBook? LibriVox)
{
    public bool Any => OpenLibraryUrl is not null || Gutenberg is not null || LibriVox is not null;
}

public interface IAvailability
{
    Task<BookAvailability> FindAsync(string title, string author, CancellationToken cancellationToken);
}

// Asked of the catalogs Shelf already uses, kept for a day so a book's page asks once.
public sealed class CatalogAvailability(IHttpClientFactory clients, IMemoryCache cache, ILogger<CatalogAvailability> logger) : IAvailability
{
    private static readonly TimeSpan Kept = TimeSpan.FromDays(1);

    public async Task<BookAvailability> FindAsync(string title, string author, CancellationToken cancellationToken)
    {
        var key = $"availability:{Tidy.Plain(title)}|{Tidy.AuthorKey(author)}";
        if (cache.TryGetValue(key, out BookAvailability? known) && known is not null)
        {
            return known;
        }

        var http = clients.CreateClient(FreeCatalog.ClientName);
        var lent = OpenLibraryAsync(http, title, author, cancellationToken);
        var gutenberg = FirstAsync(() => FreeCatalog.SearchGutenbergAsync(http, $"{title} {LastName(author)}", cancellationToken), title, author);
        var librivox = FirstAsync(() => FreeCatalog.SearchLibriVoxAsync(http, title, cancellationToken), title, author);
        await Task.WhenAll(lent, gutenberg, librivox);
        var found = new BookAvailability(lent.Result.Url, lent.Result.Free, gutenberg.Result, librivox.Result);
        cache.Set(key, found, Kept);
        return found;
    }

    private static string LastName(string author) => author.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";

    // The same book, whatever its catalog adds to the title ("Moby Dick; Or, The Whale"), by an author of the same last name.
    public static bool SameBook(string title, string author, FreeBook candidate)
    {
        var ours = Tidy.Plain(title);
        var theirs = Tidy.Plain(candidate.Title);
        var last = Tidy.Plain(author.Contains(',', StringComparison.Ordinal) ? author.Split(',')[0] : LastName(author));
        return ours.Length > 0 && (theirs == ours || theirs.StartsWith(ours + " ", StringComparison.Ordinal))
            && (last.Length == 0 || Tidy.Plain(candidate.Author).Split(' ').Contains(last.Split(' ').Last()));
    }

    private async Task<FreeBook?> FirstAsync(Func<Task<List<FreeBook>>> search, string title, string author)
    {
        try
        {
            return (await search()).FirstOrDefault(candidate => SameBook(title, author, candidate));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException or JsonException)
        {
            logger.LogDebug(ex, "A free catalog did not answer for {Title}.", title);
            return null;
        }
    }

    // Open Library's own answer for the work: free to read (public), or to borrow (borrowable).
    private async Task<(string? Url, bool Free)> OpenLibraryAsync(HttpClient http, string title, string author, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://openlibrary.org/search.json?title={Uri.EscapeDataString(title)}&author={Uri.EscapeDataString(author)}&fields=key,title,author_name,ebook_access,lending_edition_s&limit=5";
            using var response = await http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (null, false);
            }

            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            foreach (var doc in document.RootElement.GetProperty("docs").EnumerateArray())
            {
                var access = doc.TryGetProperty("ebook_access", out var given) ? given.GetString() : null;
                if (access is not ("public" or "borrowable"))
                {
                    continue;
                }

                var edition = doc.TryGetProperty("lending_edition_s", out var lending) ? lending.GetString() : null;
                var work = doc.TryGetProperty("key", out var workKey) ? workKey.GetString() : null;
                var link = edition is { Length: > 0 } ? $"https://openlibrary.org/books/{edition}" : work is { Length: > 0 } ? $"https://openlibrary.org{work}" : null;
                if (link is not null)
                {
                    return (link, access == "public");
                }
            }

            return (null, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Open Library did not answer for {Title}.", title);
            return (null, false);
        }
    }

    // Searches at libraries, for a book no catalog here has free: WorldCat for the nearest library's shelf, and
    // OverDrive (Libby's catalog) for an e-book or audiobook to borrow.
    public static string WorldCat(string title, string author) => $"https://search.worldcat.org/search?q={Uri.EscapeDataString($"ti:{title} au:{author}")}";

    public static string OverDrive(string title, string author) => $"https://www.overdrive.com/search?q={Uri.EscapeDataString($"{title} {author}")}";
}

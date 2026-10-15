using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Components.Layout;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// Somewhere to go from an address with nothing at it: the book an address of the API names, a page whose address is
// a letter or two off, and what Go to finds for the address's words.
public static partial class Lost
{
    private const int Most = 6;
    private const int MostWords = 3;
    private static readonly HashSet<string> Noise = new(["html", "htm", "php", "aspx", "index", "www", "api", "the", "and"], StringComparer.Ordinal);

    public static async Task<JumpHit[]> SuggestAsync(ShelfDb db, string path, CancellationToken cancellationToken = default)
    {
        var hits = new List<JumpHit>();

        // /books/42, as the API and older links have it, is the book at /library/42, when it is this reader's.
        if (BookAddress().Match(path) is { Success: true } match && int.TryParse(match.Groups[1].Value, out var id)
            && await db.Books.AsNoTracking().Where(book => book.Id == id).Select(book => new { book.Title, book.Author }).FirstOrDefaultAsync(cancellationToken) is { } book)
        {
            hits.Add(new JumpHit(T("Book"), book.Title, book.Author, $"/library/{id}"));
        }

        var words = Words(path);
        var admin = await ReaderRules.IsAdminAsync(db, db.ReaderId);
        var child = await Family.IsRestrictedAsync(db, cancellationToken: cancellationToken);
        var fetching = Fetcher.Ready(await FetchRules.SettingsAsync(db)) && !child;
        var pages = NavMap.Top().Concat(NavMap.Groups().SelectMany(group => group.Links)).Concat(NavMap.Elsewhere())
            .Where(link => NavMap.Allowed(link, admin, child, fetching) && !link.Href.Contains('?') && !link.Href.Contains('#') && link.Href != "/")
            .DistinctBy(link => link.Href)
            .ToList();
        hits.AddRange(pages
            .Select(link => (Link: link, Off: words.Where(word => word.Length >= 4).Select(word => Near(word, link.Href.TrimStart('/').Split('/')[0])).DefaultIfEmpty(int.MaxValue).Min()))
            .Where(item => item.Off < int.MaxValue)
            .OrderBy(item => item.Off)
            .Select(item => new JumpHit(T("Page"), item.Link.Label, null, item.Link.Href)));

        // Go to, for the words together, else for each, longest first.
        if (words.Count > 0)
        {
            var found = await Jump.FindAsync(db, string.Join(' ', words), cancellationToken);
            if (found.Length == 0)
            {
                foreach (var word in words.OrderByDescending(word => word.Length))
                {
                    found = [.. found, .. await Jump.FindAsync(db, word, cancellationToken)];
                }
            }

            hits.AddRange(found);
        }

        return [.. hits.DistinctBy(hit => hit.Href).Take(Most)];
    }

    // The words of an address worth looking for: not numbers, file endings, or bits too short to mean anything.
    public static IReadOnlyList<string> Words(string path)
    {
        string plain;
        try
        {
            plain = Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            plain = path;
        }

        return [.. Separator().Split(plain.ToLowerInvariant())
            .Where(word => word.Length >= 3 && !word.All(char.IsDigit) && !Noise.Contains(word) && word.Length <= Jump.MaxQuery)
            .Distinct(StringComparer.Ordinal)
            .Take(MostWords)];
    }

    // How far a word is from a page's address, when it is a slip of the keyboard away: one letter in a short word, two
    // in a longer one. The same word is not a slip, as Go to finds it.
    private static int Near(string word, string address)
    {
        var off = Distance(word, address);
        return off > 0 && off <= (word.Length <= 5 ? 1 : 2) ? off : int.MaxValue;
    }

    // How many letters apart two words are: added, taken away, or changed.
    private static int Distance(string one, string other)
    {
        if (Math.Abs(one.Length - other.Length) > 2)
        {
            return int.MaxValue;
        }

        var previous = Enumerable.Range(0, other.Length + 1).ToArray();
        for (var i = 1; i <= one.Length; i++)
        {
            var current = new int[other.Length + 1];
            current[0] = i;
            for (var j = 1; j <= other.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (one[i - 1] == other[j - 1] ? 0 : 1));
            }

            previous = current;
        }

        return previous[other.Length];
    }

    [GeneratedRegex(@"^/books?/(\d+)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BookAddress();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex Separator();
}

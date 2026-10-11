using System.Text.RegularExpressions;

namespace Shelf.Api.Books;

// What another app shares into Shelf, turned into the add form's address: the first real ISBN in the shared words
// or link (a bookshop's or a catalog's), or else the shared title, cleaned of the site's name.
public static partial class ShareTarget
{
    public static string Target(string? title, string? text, string? url)
    {
        var shared = string.Join(' ', new[] { title, text, url }.Where(part => !string.IsNullOrWhiteSpace(part)));
        foreach (Match candidate in IsbnLike().Matches(shared))
        {
            if (BookRules.NormalizeIsbn(candidate.Value) is { } isbn)
            {
                return $"/?add=1&isbn={isbn}";
            }
        }

        var name = SiteName().Replace((title ?? "").Trim(), "").Trim();
        if (name.Length == 0 && Uri.TryCreate(text?.Trim(), UriKind.Absolute, out _) is false)
        {
            name = (text ?? "").Trim();
        }

        if (name.Length is > 0 and <= 200)
        {
            // "The Dispossessed by Ursula K. Le Guin": a title and an author, as bookshops write them.
            var byline = ByLine().Match(name);
            return byline.Success
                ? $"/?add=1&title={Uri.EscapeDataString(byline.Groups[1].Value.Trim())}&author={Uri.EscapeDataString(byline.Groups[2].Value.Trim())}"
                : $"/?add=1&title={Uri.EscapeDataString(name)}";
        }

        return "/?add=1";
    }

    // Thirteen digits starting 978 or 979, or ten ending in a digit or X, hyphens and spaces allowed between.
    [GeneratedRegex(@"(?<![\dX])(97[89](?:[\s-]?\d){10}|\d(?:[\s-]?\d){8}[\s-]?[\dXx])(?![\dX])")]
    private static partial Regex IsbnLike();

    [GeneratedRegex(@"\s*[|:–—-]\s*(Goodreads|Amazon(\.\w+)*|Open Library|StoryGraph|Bookshop\.org|Barnes & Noble|Waterstones|Google Books|Hardcover)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex SiteName();

    [GeneratedRegex(@"^(.+?)\s+by\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ByLine();
}

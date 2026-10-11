using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

public sealed record AuthorSpelling(string Name, int Books);

// One person written more than one way across the shelf: "Le Guin, Ursula K.", "Ursula K Le Guin", "URSULA K. LE GUIN".
public sealed record AuthorGroup(string Key, IReadOnlyList<AuthorSpelling> Spellings)
{
    // The spelling most books use, the first offered to keep.
    public string Usual => Spellings.OrderByDescending(item => item.Books).ThenBy(item => item.Name.Contains(',', StringComparison.Ordinal)).ThenBy(item => item.Name, StringComparer.Ordinal).First().Name;
}

// A series named inside a title, as files and other catalogs often have it: the title it should have, and the series.
public sealed record SeriesInTitle(int BookId, string Title, string NewTitle, string Series, int? Number);

// Tidying a shelf brought in from elsewhere: one spelling for each author, and series taken out of titles.
public static partial class Tidy
{
    // The same person however written: accents, case, dots, and spacing aside, "Last, First" turned about.
    public static string AuthorKey(string author)
    {
        var name = author.Trim();
        var comma = name.IndexOf(',', StringComparison.Ordinal);
        if (comma > 0 && name.IndexOf(',', comma + 1) < 0 && !name.Contains('&', StringComparison.Ordinal) && !name.Contains(" and ", StringComparison.OrdinalIgnoreCase))
        {
            name = $"{name[(comma + 1)..]} {name[..comma]}";
        }

        var plain = new StringBuilder();
        foreach (var letter in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(letter) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            plain.Append(char.IsLetterOrDigit(letter) ? char.ToLowerInvariant(letter) : ' ');
        }

        return string.Join(' ', plain.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static async Task<IReadOnlyList<AuthorGroup>> AuthorGroupsAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var authors = await db.Books.AsNoTracking().GroupBy(book => book.Author).Select(group => new { group.Key, Count = group.Count() }).ToListAsync(cancellationToken);
        return [.. authors
            .Where(author => !string.IsNullOrWhiteSpace(author.Key) && author.Key != BookImport.UnknownAuthor)
            .GroupBy(author => AuthorKey(author.Key))
            .Where(group => group.Key.Length > 0 && group.Count() > 1)
            .Select(group => new AuthorGroup(group.Key, [.. group.Select(author => new AuthorSpelling(author.Key, author.Count)).OrderByDescending(item => item.Books)]))
            .OrderByDescending(group => group.Spellings.Sum(item => item.Books))
            .ThenBy(group => group.Key, StringComparer.Ordinal)];
    }

    // Every book under any of these spellings takes the one kept. Returns how many changed.
    public static async Task<int> UseSpellingAsync(ShelfDb db, IReadOnlyCollection<string> spellings, string keep, CancellationToken cancellationToken = default)
    {
        var name = keep.Trim();
        if (name.Length is 0 or > 200)
        {
            return 0;
        }

        var others = spellings.Where(item => item != name).ToList();
        return await db.Books.Where(book => others.Contains(book.Author)).ExecuteUpdateAsync(set => set.SetProperty(book => book.Author, name), cancellationToken);
    }

    // "Title (Series, #3)", "Title (Series Book 3)", "Series #3 - Title", "Series 03 - Title", "Series, Book 3: Title".
    [GeneratedRegex(@"^(?<title>.+?)\s*\((?<series>[^()]+?),?\s*(?:#|No\.?\s*|Book\s+|Vol\.?\s*|Volume\s+)(?<number>\d{1,3})\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingSeries();

    [GeneratedRegex(@"^(?<series>[^:#-]+?),?\s*(?:#|Book\s+|Vol\.?\s*|Volume\s+)?(?<number>\d{1,3})\s*[-–:]\s*(?<title>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingSeries();

    public static SeriesInTitle? FindSeries(int bookId, string title)
    {
        var trailing = TrailingSeries().Match(title);
        var leading = trailing.Success ? Match.Empty : LeadingSeries().Match(title);
        var match = trailing.Success ? trailing : leading;
        if (!match.Success)
        {
            return null;
        }

        var series = match.Groups["series"].Value.Trim().TrimEnd(',').Trim();
        var newTitle = match.Groups["title"].Value.Trim();
        // A leading number alone ("1984 - A Novel") is a title, not a series; the series needs letters of its own.
        if (series.Length < 2 || !series.Any(char.IsLetter) || newTitle.Length == 0 || series.Length > 200)
        {
            return null;
        }

        return new SeriesInTitle(bookId, title, newTitle, series, int.TryParse(match.Groups["number"].Value, CultureInfo.InvariantCulture, out var number) ? number : null);
    }

    public static async Task<IReadOnlyList<SeriesInTitle>> SeriesInTitlesAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        [.. (await db.Books.AsNoTracking().Where(book => book.Series == null || book.Series == "").Select(book => new { book.Id, book.Title }).ToListAsync(cancellationToken))
            .Select(book => FindSeries(book.Id, book.Title))
            .OfType<SeriesInTitle>()
            .OrderBy(item => item.Series, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Number ?? int.MaxValue)];

    public static async Task<int> ApplySeriesAsync(ShelfDb db, IEnumerable<SeriesInTitle> found, CancellationToken cancellationToken = default)
    {
        var byId = found.ToDictionary(item => item.BookId);
        var books = await db.Books.Where(book => byId.Keys.Contains(book.Id)).ToListAsync(cancellationToken);
        var changed = 0;
        foreach (var book in books)
        {
            var item = byId[book.Id];
            // Only if nothing has changed the book since it was offered.
            if (book.Title != item.Title || !string.IsNullOrWhiteSpace(book.Series))
            {
                continue;
            }

            (book.Title, book.Series, book.SeriesNumber) = (item.NewTitle[..Math.Min(item.NewTitle.Length, 200)], item.Series, item.Number);
            changed++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return changed;
    }
}

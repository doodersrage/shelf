using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public sealed record AuthorSpelling(string Name, int Books);

// One person written more than one way across the shelf: "Le Guin, Ursula K.", "Ursula K Le Guin", "URSULA K. LE GUIN".
public sealed record AuthorGroup(string Key, IReadOnlyList<AuthorSpelling> Spellings)
{
    // The spelling most books use, the first offered to keep.
    public string Usual => Spellings.OrderByDescending(item => item.Books).ThenBy(item => item.Name.Contains(',', StringComparison.Ordinal)).ThenBy(item => item.Name, StringComparer.Ordinal).First().Name;
}

// A detail that looks missing or wrong, with the books it is true of (the first of them, if many).
public sealed record CatalogFlag(string Kind, int Count, FlaggedBook[] Books);

public sealed record FlaggedBook(int Id, string Title, string Author, string? Detail);

// One publisher written more than one way: "Penguin Books", "Penguin", "PENGUIN BOOKS LTD".
public sealed record PublisherGroup(string Key, IReadOnlyList<AuthorSpelling> Spellings)
{
    public string Usual => Spellings.OrderByDescending(item => item.Books).ThenBy(item => item.Name, StringComparer.Ordinal).First().Name;
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

        return Plain(name);
    }

    // Words alone: accents, case, and punctuation aside, for comparing titles and names.
    public static string Plain(string text)
    {
        var name = text.Trim();
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

    public const int FlaggedShown = 100;

    // Gaps and oddities in the catalog, to look at one by one: nothing here changes a book.
    public static async Task<CatalogFlag[]> FlagsAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var books = await db.Books.AsNoTracking().Where(book => book.VisitingLinkId == null)
            .Select(book => new { book.Id, book.Title, book.Author, book.Isbn, book.Format, book.Series, book.SeriesNumber, book.Pages, book.EbookStoredName })
            .ToListAsync(cancellationToken);
        CatalogFlag Flag(string kind, IEnumerable<FlaggedBook> found)
        {
            var list = found.ToList();
            return new CatalogFlag(kind, list.Count, [.. list.OrderBy(book => book.Author, StringComparer.CurrentCultureIgnoreCase).ThenBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase).Take(FlaggedShown)]);
        }

        // Pages far from the other copies of the same book, or past what a book could have.
        var typical = books.Where(book => book.Pages is > 0).GroupBy(book => (AuthorKey(book.Author), Merging.BaseTitle(book.Title).Joined))
            .Where(group => group.Count() > 1).ToDictionary(group => group.Key, group => group.Select(book => book.Pages!.Value).Order().ElementAt(group.Count() / 2));
        string? Odd(int pages, string author, string title)
        {
            if (pages > 5000)
            {
                return T("{0} pages", pages);
            }

            return typical.TryGetValue((AuthorKey(author), Merging.BaseTitle(title).Joined), out var usual) && (pages > usual * 3 || pages * 3 < usual)
                ? T("{0} pages, where other copies have about {1}", pages, usual)
                : null;
        }

        return
        [
            Flag("isbn", books.Where(book => book.Isbn is null && book.Format is BookFormat.Hardcover or BookFormat.Paperback or null && book.EbookStoredName is null)
                .Select(book => new FlaggedBook(book.Id, book.Title, book.Author, null))),
            Flag("number", books.Where(book => !string.IsNullOrWhiteSpace(book.Series) && book.SeriesNumber is null)
                .Select(book => new FlaggedBook(book.Id, book.Title, book.Author, book.Series))),
            Flag("author", books.Where(book => book.Author == BookImport.UnknownAuthor || string.IsNullOrWhiteSpace(Plain(book.Author)))
                .Select(book => new FlaggedBook(book.Id, book.Title, book.Author, null))),
            Flag("pages", books.Where(book => book.Pages is int pages && Odd(pages, book.Author, book.Title) is not null)
                .Select(book => new FlaggedBook(book.Id, book.Title, book.Author, Odd(book.Pages!.Value, book.Author, book.Title)))),
        ];
    }

    public static string FlagLabel(string kind) => kind switch
    {
        "isbn" => T("Paper copies with no ISBN"),
        "number" => T("In a series, with no number"),
        "author" => T("No author known"),
        _ => T("Page counts that look wrong"),
    };

    private static readonly HashSet<string> PublisherNoise = ["inc", "ltd", "llc", "co", "company", "corp", "corporation", "limited", "group", "publishing", "publishers", "publisher", "books", "the", "an", "imprint", "of"];

    // A publisher however written: case, punctuation, and words like "Books", "Publishing", or "Ltd" aside.
    public static string PublisherKey(string publisher)
    {
        var words = Plain(publisher.Replace("&", " and ", StringComparison.Ordinal)).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(word => !PublisherNoise.Contains(word)).ToList();
        return words.Count == 0 ? Plain(publisher) : string.Join(' ', words);
    }

    public static async Task<IReadOnlyList<PublisherGroup>> PublisherGroupsAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var publishers = await db.Books.AsNoTracking().Where(book => book.Publisher != null).GroupBy(book => book.Publisher!)
            .Select(group => new { group.Key, Count = group.Count() }).ToListAsync(cancellationToken);
        return [.. publishers
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => PublisherKey(item.Key))
            .Where(group => group.Key.Length > 0 && group.Count() > 1)
            .Select(group => new PublisherGroup(group.Key, [.. group.Select(item => new AuthorSpelling(item.Key, item.Count)).OrderByDescending(item => item.Books)]))
            .OrderByDescending(group => group.Spellings.Sum(item => item.Books))
            .ThenBy(group => group.Key, StringComparer.Ordinal)];
    }

    public static async Task<int> UsePublisherAsync(ShelfDb db, IReadOnlyCollection<string> spellings, string keep, CancellationToken cancellationToken = default)
    {
        var name = keep.Trim();
        if (name.Length is 0 or > 200)
        {
            return 0;
        }

        var others = spellings.Where(item => item != name).ToList();
        var books = await db.Books.Where(book => book.Publisher != null && others.Contains(book.Publisher)).ToListAsync(cancellationToken);
        foreach (var book in books)
        {
            book.Publisher = name;
        }

        await db.SaveChangesAsync(cancellationToken);
        return books.Count;
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

        // Saved book by book, not as one bulk update, so each change is kept in history and the batch can be undone.
        var others = spellings.Where(item => item != name).ToList();
        var books = await db.Books.Where(book => others.Contains(book.Author)).ToListAsync(cancellationToken);
        foreach (var book in books)
        {
            book.Author = name;
        }

        await db.SaveChangesAsync(cancellationToken);
        return books.Count;
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

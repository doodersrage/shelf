using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

// The words of one chapter of an EPUB, or one page of a PDF, kept for searching inside books.
// It belongs to a reading of one file, so a new upload replaces it.
public sealed class BookText
{
    public int Id { get; set; }
    public int ScanId { get; set; }
    public int BookId { get; set; }
    public int Part { get; set; }
    public required string Text { get; set; }
}

public sealed record SearchHit(int BookId, string Title, string Author, string? CoverUrl, int Part, string Where, string Before, string Match, string After, string Open);

public static partial class Search
{
    public const int MinLength = 2;
    private const int Around = 90;

    public static string PlainText(string html)
    {
        var withoutHead = HeadPattern().Replace(html, " ");
        var text = WebUtility.HtmlDecode(TagPattern().Replace(withoutHead, " "));
        return SpacePattern().Replace(text, " ").Trim();
    }

    // Inside every book the reader may open: their own, and those lent to them. The full-text index ranks the
    // places, ignores case and accents, and lets the last word be partial; up to three places a book.
    public static async Task<SearchHit[]> FindAsync(ShelfDb db, string? query, CancellationToken cancellationToken = default)
    {
        var term = SpacePattern().Replace(query?.Trim() ?? "", " ");
        var me = db.ReaderId;
        if (term.Length < MinLength || me == 0)
        {
            return [];
        }

        var match = "\"" + term.Replace("\"", "\"\"") + "\"*";
        List<RankedText> ranked;
        try
        {
            ranked = await db.Database
                .SqlQuery<RankedText>($"SELECT rowid AS \"Id\", bm25(\"BookTextSearch\") AS \"Rank\" FROM \"BookTextSearch\" WHERE \"BookTextSearch\" MATCH {match} ORDER BY \"Rank\" LIMIT 2000")
                .ToListAsync(cancellationToken);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Words the index cannot read as a phrase, such as only punctuation, find nothing.
            return [];
        }

        if (ranked.Count == 0)
        {
            return [];
        }

        var order = ranked.Select((row, index) => (row.Id, index)).ToDictionary(item => item.Id, item => item.index);
        var ids = order.Keys.ToList();
        var rows = await (
                from text in db.BookTexts.AsNoTracking()
                join scan in db.OcrScans.AsNoTracking() on text.ScanId equals scan.Id
                join book in db.Books.IgnoreQueryFilters().AsNoTracking() on text.BookId equals book.Id
                where ids.Contains(text.Id)
                    && (book.OwnerId == me || book.BorrowerId == me)
                    && book.EbookStoredName == scan.StoredName
                select new { TextId = text.Id, book.Id, book.Title, book.Author, book.CoverUrl, book.EbookStoredName, text.Part, text.Text })
            .ToListAsync(cancellationToken);

        return rows
            .OrderBy(row => order[row.TextId])
            .GroupBy(row => row.Id)
            .Take(50)
            .SelectMany(group => group.Take(3))
            .Select(row =>
            {
                var (at, length) = Locate(row.Text, term);
                var start = Math.Max(0, at - Around);
                var end = Math.Min(row.Text.Length, at + length + Around);
                var where = EbookStore.IsPdf(row.EbookStoredName) ? $"Page {row.Part + 1}" : $"Chapter {row.Part + 1}";
                return new SearchHit(
                    row.Id,
                    row.Title,
                    row.Author,
                    row.CoverUrl,
                    row.Part,
                    where,
                    (start > 0 ? "…" : "") + row.Text[start..at],
                    row.Text.Substring(at, length),
                    row.Text[(at + length)..end] + (end < row.Text.Length ? "…" : ""),
                    $"/library/{row.Id}/read?chapter={row.Part}");
            })
            .ToArray();
    }

    // Where the words sit in the text, ignoring case and accents as the index does; the first word alone, or
    // the start of the text, when punctuation between the words keeps the whole phrase from lining up.
    private static (int At, int Length) Locate(string text, string term)
    {
        var compare = System.Globalization.CultureInfo.InvariantCulture.CompareInfo;
        const System.Globalization.CompareOptions loose = System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace;
        var at = compare.IndexOf(text, term, loose, out var length);
        if (at >= 0)
        {
            return (at, length);
        }

        var first = term.Split(' ')[0];
        at = compare.IndexOf(text, first, loose, out length);
        return at >= 0 ? (at, length) : (0, 0);
    }

    private sealed class RankedText
    {
        public int Id { get; set; }

        public double Rank { get; set; }
    }

    public static async Task<Ok<SearchHit[]>> Find(ShelfDb db, CancellationToken cancellationToken, string? q = null) =>
        TypedResults.Ok(await FindAsync(db, q, cancellationToken));

    [GeneratedRegex(@"<head[\s\S]*?</head>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadPattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacePattern();
}

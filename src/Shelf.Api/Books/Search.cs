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

    // Inside every book the reader may open: their own, and those lent to them. Up to three places a book.
    public static async Task<SearchHit[]> FindAsync(ShelfDb db, string? query, CancellationToken cancellationToken = default)
    {
        var term = query?.Trim() ?? "";
        var me = db.ReaderId;
        if (term.Length < MinLength || me == 0)
        {
            return [];
        }

        var pattern = "%" + term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
        var rows = await (
                from text in db.BookTexts.AsNoTracking()
                join scan in db.OcrScans.AsNoTracking() on text.ScanId equals scan.Id
                join book in db.Books.IgnoreQueryFilters().AsNoTracking() on text.BookId equals book.Id
                where (book.OwnerId == me || book.BorrowerId == me)
                    && book.EbookStoredName == scan.StoredName
                    && EF.Functions.Like(text.Text, pattern, @"\")
                orderby book.Title, text.Part
                select new { book.Id, book.Title, book.Author, book.CoverUrl, book.EbookStoredName, text.Part, text.Text })
            .Take(400)
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.Id)
            .Take(50)
            .SelectMany(group => group.Take(3))
            .Select(row =>
            {
                var at = row.Text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                at = Math.Max(0, at);
                var start = Math.Max(0, at - Around);
                var end = Math.Min(row.Text.Length, at + term.Length + Around);
                var where = EbookStore.IsPdf(row.EbookStoredName) ? $"Page {row.Part + 1}" : $"Chapter {row.Part + 1}";
                return new SearchHit(
                    row.Id,
                    row.Title,
                    row.Author,
                    row.CoverUrl,
                    row.Part,
                    where,
                    (start > 0 ? "…" : "") + row.Text[start..at],
                    row.Text.Substring(at, Math.Min(term.Length, row.Text.Length - at)),
                    row.Text[(at + Math.Min(term.Length, row.Text.Length - at))..end] + (end < row.Text.Length ? "…" : ""),
                    $"/library/{row.Id}/read?chapter={row.Part}");
            })
            .ToArray();
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

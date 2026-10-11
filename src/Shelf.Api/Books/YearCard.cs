using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// A reader's year in books as a picture to share: the numbers, and the covers of what they finished. Drawn as an SVG
// at 1080 by 1350 (a portrait post) with the covers inside it, so the browser can turn it into a PNG without asking
// any other server; year-card.js does that, and offers it to share or save.
public static class YearCard
{
    private const int Width = 1080;
    private const int Height = 1350;
    private const int MaxCovers = 12;

    public static void Map(RouteGroupBuilder books) =>
        books.MapGet("/years/{year:int}/card.svg", async (int year, ShelfDb db, EbookStore ebooks, CoverStore covers, IHttpClientFactory clients, CancellationToken cancellationToken) =>
        {
            if (year is < 1900 or > 2200)
            {
                return Results.NotFound();
            }

            var review = await YearReview.BuildAsync(db, year, cancellationToken);
            var shelf = await db.Books.AsNoTracking().Where(book => review.Books.Select(item => item.Id).Contains(book.Id)).ToDictionaryAsync(book => book.Id, cancellationToken);
            var pictures = await CoversAsync(review.Books.Select(item => shelf.GetValueOrDefault(item.Id)).OfType<Book>().Take(MaxCovers), ebooks, covers, clients, cancellationToken);
            return Results.Text(Draw(review, pictures), "image/svg+xml; charset=utf-8");
        });

    // The covers as data, in the order the books were finished: a picture of the shelf's own, or one fetched from
    // the address it was linked from (a few seconds at most); a book with neither shows its title on a spine.
    private static async Task<List<(Book Book, string? Data)>> CoversAsync(IEnumerable<Book> books, EbookStore ebooks, CoverStore covers, IHttpClientFactory clients, CancellationToken cancellationToken)
    {
        var http = clients.CreateClient(FreeCatalog.ClientName);
        var tasks = books.Select(async book =>
        {
            if (Covers.Picture(book, ebooks, covers) is { } bytes)
            {
                return (book, DataUri(bytes));
            }

            if (book.CoverUrl is { Length: > 0 } url && Uri.TryCreate(url, UriKind.Absolute, out var address) && address.Scheme is "https" or "http")
            {
                try
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    limit.CancelAfter(TimeSpan.FromSeconds(5));
                    using var response = await http.GetAsync(address, limit.Token);
                    if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength is null or <= CoverStore.MaxBytes)
                    {
                        return (book, (string?)DataUri(await response.Content.ReadAsByteArrayAsync(limit.Token)));
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    // The spine stands in.
                }
            }

            return (book, (string?)null);
        });
        return [.. await Task.WhenAll(tasks)];
    }

    private static string DataUri(byte[] bytes)
    {
        var start = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 200)).TrimStart();
        var kind = start.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) || start.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ? "image/svg+xml"
            : bytes.Length > 3 && bytes[0] == 0x89 && bytes[1] == 0x50 ? "image/png"
            : bytes.Length > 3 && bytes[0] == 0x47 && bytes[1] == 0x49 ? "image/gif"
            : bytes.Length > 11 && bytes[8] == 0x57 && bytes[9] == 0x45 ? "image/webp"
            : "image/jpeg";
        return $"data:{kind};base64,{Convert.ToBase64String(bytes)}";
    }

    private static string X(string text) => WebUtility.HtmlEncode(text);

    private static string Fit(string text, int most) => text.Length <= most ? text : text[..(most - 1)].TrimEnd() + "…";

    // A title on a spine, a few words to a line, the last cut short when it runs over.
    public static List<string> Wrap(string text, int width, int most)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                lines.Add(line);
                line = word;
            }
            else
            {
                line = line.Length == 0 ? word : $"{line} {word}";
            }
        }

        if (line.Length > 0)
        {
            lines.Add(line);
        }

        if (lines.Count > most)
        {
            lines = lines.Take(most).ToList();
            lines[^1] = lines[^1].TrimEnd('.', ',') + "…";
        }

        return lines;
    }

    public static string Draw(YearReviewResponse review, IReadOnlyList<(Book Book, string? Data)> pictures)
    {
        var hours = (review.ReadingSeconds + review.ListeningSeconds) / 3600;
        var numbers = new List<(string Value, string Label)>
        {
            (review.Finished.ToString("N0", CultureInfo.CurrentCulture), review.Finished == 1 ? T("book finished") : T("books finished")),
        };
        if (review.Pages > 0)
        {
            numbers.Add((review.Pages.ToString("N0", CultureInfo.CurrentCulture), T("pages")));
        }

        if (hours > 0)
        {
            numbers.Add((hours.ToString("N0", CultureInfo.CurrentCulture), hours == 1 ? T("hour with books") : T("hours with books")));
        }

        if (review.Authors > 0)
        {
            numbers.Add((review.Authors.ToString("N0", CultureInfo.CurrentCulture), review.Authors == 1 ? T("author") : T("authors")));
        }

        var svg = new StringBuilder();
        svg.Append($"""<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="{Width}" height="{Height}" viewBox="0 0 {Width} {Height}">""");
        svg.Append("""<defs><clipPath id="r"><rect width="180" height="270" rx="10"/></clipPath></defs>""");
        svg.Append($"""<rect width="{Width}" height="{Height}" fill="#f5f1e8"/>""");
        svg.Append($"""<rect x="0" y="0" width="{Width}" height="16" fill="#263e38"/>""");
        svg.Append($"""<text x="80" y="150" font-family="Lora, Georgia, 'Times New Roman', serif" font-size="76" font-weight="700" fill="#252a28">{X(T("My {0} in books", review.Year))}</text>""");

        // The numbers, side by side.
        var column = (Width - 160) / Math.Max(1, numbers.Count);
        for (var index = 0; index < numbers.Count; index++)
        {
            var x = 80 + index * column;
            svg.Append($"""<text x="{x}" y="290" font-family="Lora, Georgia, serif" font-size="84" font-weight="700" fill="#263e38">{X(numbers[index].Value)}</text>""");
            svg.Append($"""<text x="{x}" y="335" font-family="Inter, 'Helvetica Neue', Arial, sans-serif" font-size="28" fill="#66716b">{X(numbers[index].Label)}</text>""");
        }

        if (review.Goal > 0)
        {
            var share = Math.Min(1, review.Finished / (double)review.Goal);
            svg.Append($"""<rect x="80" y="380" width="{Width - 160}" height="14" rx="7" fill="#ece6d9"/>""");
            svg.Append($"""<rect x="80" y="380" width="{(int)((Width - 160) * share)}" height="14" rx="7" fill="#263e38"/>""");
            svg.Append($"""<text x="80" y="432" font-family="Inter, 'Helvetica Neue', Arial, sans-serif" font-size="26" fill="#66716b">{X(T("{0} of the {1} I set out to read", review.Finished, review.Goal))}</text>""");
        }

        // Up to twelve covers, four to a row.
        for (var index = 0; index < pictures.Count; index++)
        {
            var (book, data) = pictures[index];
            var x = 80 + index % 4 * 240 + 15;
            var y = 480 + index / 4 * 290;
            svg.Append($"""<g transform="translate({x} {y})">""");
            if (data is not null)
            {
                svg.Append($"""<image width="180" height="270" preserveAspectRatio="xMidYMid slice" clip-path="url(#r)" href="{data}" xlink:href="{data}"/>""");
            }
            else
            {
                svg.Append("""<rect width="180" height="270" rx="10" fill="#263e38"/>""");
                var lines = Wrap(book.Title, 13, 6);
                var top = 135 - lines.Count * 15;
                for (var line = 0; line < lines.Count; line++)
                {
                    svg.Append($"""<text x="90" y="{top + line * 30}" text-anchor="middle" font-family="Georgia, serif" font-size="22" font-weight="600" fill="#f5f1e8">{X(lines[line])}</text>""");
                }
            }

            svg.Append("</g>");
        }

        var below = 480 + (pictures.Count + 3) / 4 * 290 + 20;
        if (review.Books.Length > pictures.Count)
        {
            svg.Append($"""<text x="80" y="{below}" font-family="Inter, 'Helvetica Neue', Arial, sans-serif" font-size="26" fill="#66716b">{X(T("and {0} more", review.Books.Length - pictures.Count))}</text>""");
            below += 50;
        }

        // What stood out, where there is room: the longest book, and a line kept from the year.
        if (review.Longest is { Pages: int longestPages } longest && below < Height - 260 && review.Finished > 1)
        {
            below += 30;
            svg.Append($"""<text x="80" y="{below}" font-family="Inter, 'Helvetica Neue', Arial, sans-serif" font-size="26" fill="#66716b">{X(T("Longest"))}</text>""");
            svg.Append($"""<text x="80" y="{below + 44}" font-family="Lora, Georgia, serif" font-size="36" font-weight="600" fill="#252a28">{X(Fit(longest.Title, 40))} · {X(T("{0} pages", longestPages))}</text>""");
            below += 100;
        }

        if (review.FavouriteQuote is { Length: > 0 and <= 180 } quote && below < Height - 220)
        {
            below += 20;
            var lines = Wrap($"“{quote}”", 44, 4);
            for (var line = 0; line < lines.Count; line++)
            {
                svg.Append($"""<text x="80" y="{below + line * 46}" font-family="Lora, Georgia, serif" font-size="34" font-style="italic" fill="#252a28">{X(lines[line])}</text>""");
            }

            if (review.FavouriteQuoteBook is { } source)
            {
                svg.Append($"""<text x="80" y="{below + lines.Count * 46 + 10}" font-family="Inter, 'Helvetica Neue', Arial, sans-serif" font-size="24" fill="#66716b">{X(Fit(source, 60))}</text>""");
            }
        }

        svg.Append($"""<text x="{Width - 80}" y="{Height - 60}" text-anchor="end" font-family="Inter, 'Helvetica Neue', Arial, sans-serif" font-size="34" font-weight="700" fill="#263e38">shelf</text>""");
        svg.Append("</svg>");
        return svg.ToString();
    }
}

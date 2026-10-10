using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Shelf.Api.Books;

public sealed record CsvBook(BookWrite Write, DateTimeOffset? AddedAt);

public sealed record CsvLibrary(string Source, IReadOnlyList<CsvBook> Books);

// Reads a library exported from Goodreads or The StoryGraph into books for this shelf.
public static partial class LibraryCsv
{
    public static CsvLibrary? Read(string text)
    {
        var rows = Parse(text);
        if (rows.Count == 0)
        {
            return null;
        }

        var header = rows[0].Select(name => name.Trim()).ToList();
        int Column(string name) => header.FindIndex(item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (Column("Exclusive Shelf") >= 0 && Column("Title") >= 0 && Column("Author") >= 0)
        {
            return new CsvLibrary("Goodreads", rows.Skip(1).Select(row => Goodreads(row, Column)).OfType<CsvBook>().ToList());
        }

        if (Column("Read Status") >= 0 && Column("Title") >= 0 && Column("Authors") >= 0)
        {
            return new CsvLibrary("StoryGraph", rows.Skip(1).Select(row => StoryGraph(row, Column)).OfType<CsvBook>().ToList());
        }

        return null;
    }

    private static CsvBook? Goodreads(string[] row, Func<string, int> column)
    {
        string? Cell(string name) => Value(row, column(name));
        var (title, series, number) = SplitSeries(Cell("Title"));
        var author = Cell("Author");
        if (title is null || author is null)
        {
            return null;
        }

        var shelf = Cell("Exclusive Shelf")?.ToLowerInvariant();
        var status = shelf switch
        {
            "read" => BookStatus.Finished,
            "currently-reading" => BookStatus.Reading,
            "to-read" or null => BookStatus.Want,
            _ => BookStatus.Abandoned,
        };
        var finished = status == BookStatus.Finished ? Date(Cell("Date Read")) : null;
        var exclusive = new[] { "read", "currently-reading", "to-read", shelf };
        var tags = (Cell("Bookshelves") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => !exclusive.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .Select(tag => tag.Replace('-', ' '))
            .ToList();
        var write = BookWrite.From(
            title,
            author,
            status,
            Rating(Cell("My Rating")),
            Year(Cell("Original Publication Year")) ?? Year(Cell("Year Published")),
            Isbn(Cell("ISBN13")) ?? Isbn(Cell("ISBN")),
            Pages(Cell("Number of Pages")),
            null,
            Clip(Cell("Private Notes"), 4000),
            finished,
            finished,
            null,
            Tags(tags),
            null,
            Clip(Cell("Publisher"), 200),
            null,
            BookRules.ParseFormat(Cell("Binding")),
            series,
            series is null ? null : number,
            null,
            Clip(Review(Cell("My Review")), 4000),
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            null,
            null);
        return new CsvBook(write, Moment(Cell("Date Added")));
    }

    private static CsvBook? StoryGraph(string[] row, Func<string, int> column)
    {
        string? Cell(string name) => Value(row, column(name));
        var title = Clip(Cell("Title"), 200);
        var author = Clip(Cell("Authors"), 200);
        if (title is null || author is null)
        {
            return null;
        }

        var status = Cell("Read Status")?.ToLowerInvariant() switch
        {
            "read" => BookStatus.Finished,
            "currently-reading" => BookStatus.Reading,
            "did-not-finish" => BookStatus.Abandoned,
            _ => BookStatus.Want,
        };
        var finished = status == BookStatus.Finished ? Date(Cell("Last Date Read")) : null;
        var format = Cell("Format")?.ToLowerInvariant() switch
        {
            "digital" => BookFormat.Ebook,
            "audio" => BookFormat.Audiobook,
            var other => BookRules.ParseFormat(other),
        };
        var tags = (Cell("Tags") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var write = BookWrite.From(
            title,
            author,
            status,
            Rating(Cell("Star Rating")),
            null,
            Isbn(Cell("ISBN/UID")),
            null,
            null,
            null,
            finished,
            finished,
            null,
            Tags(tags),
            null,
            null,
            null,
            format,
            null,
            null,
            null,
            Clip(Review(Cell("Review")), 4000),
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            null,
            null);
        return new CsvBook(write, Moment(Cell("Date Added")));
    }

    // RFC 4180: commas separate fields, quotes wrap fields that hold commas, quotes, or line breaks.
    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quoted)
            {
                if (character == '"' && index + 1 < text.Length && text[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else if (character == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(character);
                }

                continue;
            }

            switch (character)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add([.. row]);
                    row.Clear();
                    break;
                default:
                    field.Append(character);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add([.. row]);
        }

        return rows.Where(item => item.Any(cell => cell.Length > 0)).ToList();
    }

    // Goodreads writes titles as "A Wizard of Earthsea (Earthsea Cycle, #1)".
    private static (string? Title, string? Series, int? Number) SplitSeries(string? value)
    {
        if (value is null)
        {
            return (null, null, null);
        }

        var match = SeriesPattern().Match(value);
        if (!match.Success)
        {
            return (Clip(value, 200), null, null);
        }

        var number = int.TryParse(match.Groups["number"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole) && whole is >= 1 and <= 999
            ? whole
            : (int?)null;
        return (Clip(match.Groups["title"].Value, 200), Clip(match.Groups["series"].Value, 200), number);
    }

    [GeneratedRegex(@"^(?<title>.+?)\s*\((?<series>[^()#]+?),?\s*#(?<number>[\d.]+)\)\s*$")]
    private static partial Regex SeriesPattern();

    private static string? Value(string[] row, int index)
    {
        if (index < 0 || index >= row.Length)
        {
            return null;
        }

        var text = row[index].Trim();
        // Goodreads guards ISBNs from spreadsheets as ="0441478123".
        if (text.StartsWith("=\"", StringComparison.Ordinal) && text.EndsWith('"'))
        {
            text = text[2..^1];
        }

        return text.Length == 0 ? null : text;
    }

    private static string? Clip(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim() is var trimmed && trimmed.Length > max ? trimmed[..max] : value.Trim();

    private static string? Isbn(string? value) => BookRules.NormalizeIsbn(value);

    private static int? Rating(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var stars) && stars > 0
            ? Math.Clamp((int)Math.Round(stars, MidpointRounding.AwayFromZero), 1, 5)
            : null;

    private static int? Year(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) && year is >= 1000 and <= 2100 ? year : null;

    private static int? Pages(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pages) && pages is >= 1 and <= 20000 ? pages : null;

    private static readonly string[] DateFormats = ["yyyy/MM/dd", "yyyy-MM-dd", "yyyy/M/d", "MM/dd/yyyy"];

    private static DateOnly? Date(string? value) =>
        DateOnly.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static DateTimeOffset? Moment(string? value) =>
        Date(value) is DateOnly date ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null;

    private static string? Review(string? value) =>
        value is null ? null : BreakPattern().Replace(value, "\n").Trim();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakPattern();

    private static string[] Tags(IEnumerable<string> tags) =>
        BookRules.CanonicalTags(tags.ToList())
            .Where(tag => tag.Length <= BookRules.MaxTagLength)
            .Take(BookRules.MaxTags)
            .ToArray();
}

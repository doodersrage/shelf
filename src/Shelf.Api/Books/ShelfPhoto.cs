using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

// A book whose spine a photo seems to show, how sure, and whether a shelf check now counts it seen.
public sealed record SpineMatch(int BookId, string Title, string Author, string? Location, int Score, bool Seen);

// What a photo of a shelf was read as: books of the reader's it seems to show, and lines of text that look like a
// spine but match none of them, to look up.
public sealed record ShelfPhotoResult(bool CanRead, SpineMatch[] Matches, string[] Unmatched);

// A photo of the spines on a shelf, read by Tesseract on the server and matched to the catalog. Spines stand on
// end, so the page sends each photo turned three ways; every line found is matched on the distinctive words of a
// title, a slip of a letter forgiven in a longer word, and the author's surname makes a short title sure.
public static class ShelfPhotos
{
    public const int MaxPhotos = 6;
    public const long MaxBytes = 15 * 1024 * 1024;
    public const int SureScore = 60;

    // A word in more titles than this does not by itself bring books to weigh.
    private const int CommonWord = 1500;

    // More books than this fitting a line equally well, and it is taken as saying none of them.
    private const int MostAlike = 2;

    // How sure Tesseract must be of a line, out of 100, for it to be offered to look up, and how far below the
    // clearest image sent the image it came from may be: text read upside down, as the turn of a photo that doesn't
    // suit its spines is, comes out as letters it is less sure of, though now and then one line of it is sure enough.
    public const int ClearLine = 75;
    private const int ClearerBy = 20;

    private static readonly HashSet<string> Small = ["the", "and", "for", "with", "from", "into", "that", "this", "les", "des", "une", "der", "die", "das", "und", "los", "las", "del", "book", "novel", "edition", "volume"];

    // A title's words worth matching on: letters and digits, three or more, not the small ones every title has.
    public static string[] Distinctive(string text) =>
        [.. Tidy.Plain(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(word => word.Length >= 3 && !Small.Contains(word)).Distinct()];

    // "Ursula K. Le Guin" and "Le Guin, Ursula K." both give "guin": the last word before any comma, of the first author.
    public static string Surname(string author) =>
        Tidy.Plain(author.Split([",", "&", ";", " and "], StringSplitOptions.TrimEntries)[0]).Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";

    // Each line with the next, as a title often runs onto a second line of the same spine.
    public static List<(SpineMatch Match, int Line)> Match(IReadOnlyList<string> lines, IReadOnlyList<(int Id, string Title, string Author, string? Location)> books)
    {
        var windows = lines.Select((line, at) => (Words: Distinctive(line + " " + (at + 1 < lines.Count ? lines[at + 1] : "")), Line: at)).ToList();
        var index = new Dictionary<string, List<int>>();
        var titles = new Dictionary<int, string[]>();
        for (var at = 0; at < books.Count; at++)
        {
            var words = Distinctive(Merging.BaseTitle(books[at].Title).Joined);
            titles[at] = words;
            foreach (var word in words)
            {
                (index.TryGetValue(word, out var list) ? list : index[word] = []).Add(at);
            }
        }

        var best = new Dictionary<int, (int Score, int Line)>();
        foreach (var (words, line) in windows)
        {
            // Words in many titles ("love", "life") bring too many books to weigh; the rarer ones say which.
            var near = new HashSet<string>(words);
            var telling = words.Where(word => index.TryGetValue(word, out var list) && list.Count <= CommonWord).ToList();
            if (telling.Count == 0 && words.Where(index.ContainsKey).MinBy(word => index[word].Count) is { } rarest)
            {
                telling.Add(rarest);
            }

            var candidates = new HashSet<int>();
            foreach (var word in telling)
            {
                candidates.UnionWith(index[word]);
            }

            var scored = new List<(int Book, int Score)>();
            foreach (var at in candidates)
            {
                var title = titles[at];
                var hits = title.Count(word => near.Contains(word) || (word.Length >= 5 && words.Any(other => other.Length >= 4 && Close(word, other))));
                var surname = Surname(books[at].Author);
                var byAuthor = surname.Length >= 3 && words.Any(word => word == surname || (surname.Length >= 5 && Close(word, surname)));
                var share = (double)hits / title.Length;

                // A one-word title needs its author beside it; a longer one most of its words.
                var score = title.Length switch
                {
                    0 => 0,
                    1 => hits == 1 && byAuthor && title[0].Length >= 4 ? 80 : 0,
                    _ when hits >= 2 && share >= 0.75 => (int)Math.Round(share * 80) + (byAuthor ? 20 : 0),
                    _ when hits >= 2 && share >= 0.5 && byAuthor => (int)Math.Round(share * 70) + 20,
                    _ => 0,
                };
                if (score >= SureScore)
                {
                    scored.Add((at, score));
                }
            }

            // A line is one spine: its best match, unless several books fit it as well and it can't say which.
            if (scored.Count == 0)
            {
                continue;
            }

            var top = scored.Max(item => item.Score);
            var leaders = scored.Where(item => item.Score == top).ToList();
            if (leaders.Count > MostAlike)
            {
                continue;
            }

            foreach (var (at, score) in leaders)
            {
                if (!best.TryGetValue(at, out var kept) || kept.Score < score)
                {
                    best[at] = (score, line);
                }
            }
        }

        return [.. best.OrderByDescending(pair => pair.Value.Score).Select(pair => (new SpineMatch(books[pair.Key].Id, books[pair.Key].Title, books[pair.Key].Author, books[pair.Key].Location, Math.Min(100, pair.Value.Score), false), pair.Value.Line))];
    }

    // One letter put in, left out, or read wrong.
    private static bool Close(string one, string other)
    {
        if (Math.Abs(one.Length - other.Length) > 1)
        {
            return false;
        }

        var (i, j, edits) = (0, 0, 0);
        while (i < one.Length && j < other.Length)
        {
            if (one[i] == other[j])
            {
                (i, j) = (i + 1, j + 1);
                continue;
            }

            if (++edits > 1)
            {
                return false;
            }

            if (one.Length > other.Length)
            {
                i++;
            }
            else if (other.Length > one.Length)
            {
                j++;
            }
            else
            {
                (i, j) = (i + 1, j + 1);
            }
        }

        return edits + (one.Length - i) + (other.Length - j) <= 1;
    }

    public static async Task<ShelfPhotoResult> ReadAsync(ShelfDb db, OcrTools tools, IReadOnlyList<IFormFile> files, int? scanId, CancellationToken cancellationToken = default)
    {
        if (!tools.Available)
        {
            return new ShelfPhotoResult(false, [], []);
        }

        var images = new List<List<(string Text, double Confidence)>>();
        foreach (var file in files.Take(MaxPhotos * 3))
        {
            if (file.Length is 0 or > MaxBytes)
            {
                continue;
            }

            var path = Path.Combine(Path.GetTempPath(), $"shelf-spines-{Guid.NewGuid():N}");
            try
            {
                await using (var target = File.Create(path))
                {
                    await file.CopyToAsync(target, cancellationToken);
                }

                var head = new byte[16];
                await using (var check = File.OpenRead(path))
                {
                    await check.ReadExactlyAsync(head.AsMemory(0, (int)Math.Min(16, check.Length)), cancellationToken);
                }

                if (CoverStore.Kind(head) is null)
                {
                    continue;
                }

                // Sparse text, as spines are: words wherever they stand, in no page order.
                var text = await Tools.RunAsync(tools.Tesseract, [path, "stdout", "-l", tools.Languages, "--psm", "11", "tsv"], TimeSpan.FromSeconds(90), cancellationToken);
                images.Add([.. Lines(text ?? "").Where(line => line.Text.Count(char.IsLetter) >= 3)]);
            }
            finally
            {
                File.Delete(path);
            }
        }

        // An image's clarity: how sure Tesseract was of its letters, all told.
        static double Clarity(List<(string Text, double Confidence)> lines) =>
            lines.Sum(line => line.Text.Length) is var letters and > 0 ? lines.Sum(line => line.Confidence * line.Text.Length) / letters : 0;
        var clearest = images.Count == 0 ? 0 : images.Max(Clarity);
        var lines = images.SelectMany(image => image.Select(line => (line.Text, Clear: line.Confidence >= ClearLine && Clarity(image) >= clearest - ClearerBy))).ToList();
        return await MatchAsync(db, [.. lines.Select(line => line.Text)], scanId, cancellationToken, [.. lines.Select(line => line.Clear)]);
    }

    // Tesseract's TSV, one word to a row, gathered into its lines, each with how sure it was of their words.
    public static List<(string Text, double Confidence)> Lines(string tsv)
    {
        var lines = new List<(string Key, List<(string Word, double Confidence)> Words)>();
        foreach (var row in tsv.Split('\n').Skip(1))
        {
            var cells = row.TrimEnd('\r').Split('\t');
            if (cells.Length < 12 || string.IsNullOrWhiteSpace(cells[11])
                || !double.TryParse(cells[10], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var confidence) || confidence < 0)
            {
                continue;
            }

            var key = $"{cells[1]}.{cells[2]}.{cells[3]}.{cells[4]}";
            if (lines.Count == 0 || lines[^1].Key != key)
            {
                lines.Add((key, []));
            }

            lines[^1].Words.Add((cells[11].Trim(), confidence));
        }

        return [.. lines.Select(line => (string.Join(' ', line.Words.Select(word => word.Word)), line.Words.Average(word => word.Confidence)))];
    }

    // Lines read from a photo matched to the reader's books; in a shelf check, the books matched surely are seen.
    // Only clear lines, where it is said which are, are offered to look up, and none that only names a book found.
    public static async Task<ShelfPhotoResult> MatchAsync(ShelfDb db, IReadOnlyList<string> lines, int? scanId, CancellationToken cancellationToken = default, IReadOnlyList<bool>? clear = null)
    {
        var books = (await db.Books.AsNoTracking().Select(book => new { book.Id, book.Title, book.Author, book.Location }).ToListAsync(cancellationToken))
            .Select(book => (book.Id, book.Title, book.Author, book.Location)).ToList();
        var found = Match(lines, books);
        var session = scanId is int id ? await Scanning.FindAsync(db, id, cancellationToken) : null;
        var matches = new List<SpineMatch>();
        foreach (var (match, _) in found)
        {
            var seen = session is { Kind: ScanKind.Check } && string.Equals(match.Location?.Trim(), session.Location?.Trim(), StringComparison.CurrentCultureIgnoreCase)
                && await Scanning.MarkSeenAsync(db, session.Id, match.BookId, moveHere: false, cancellationToken);
            matches.Add(match with { Seen = seen });
        }

        var used = found.Select(item => item.Line).ToHashSet();
        var named = found.Select(item => Distinctive(item.Match.Title + " " + item.Match.Author).ToHashSet()).ToList();
        bool Names(string line) => Distinctive(line) is var words && named.Any(book => words.Count(book.Contains) * 2 >= words.Length);
        var unmatched = lines.Select((line, at) => (line, at))
            .Where(item => !used.Contains(item.at) && (clear is null || clear[item.at]) && Distinctive(item.line).Length >= 2 && !Names(item.line))
            .Select(item => item.line).Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToArray();
        return new ShelfPhotoResult(true, [.. matches], unmatched);
    }

    public static void Map(RouteGroupBuilder books) =>
        books.MapPost("/scans/photo", async Task<Ok<ShelfPhotoResult>> (IFormFileCollection files, [Microsoft.AspNetCore.Mvc.FromForm] int? scanId, ShelfDb db, OcrTools tools, CancellationToken cancellationToken) =>
            TypedResults.Ok(await ReadAsync(db, tools, [.. files], scanId, cancellationToken))).DisableAntiforgery().WithTags("Scanning");
}

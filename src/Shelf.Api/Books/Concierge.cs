using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public enum AskLooksFor
{
    Books,
    Quotes,
}

// A question as Shelf understood it: what to look for, and what it must be.
public sealed record AskQuery(
    AskLooksFor LooksFor = AskLooksFor.Books,
    string[]? Words = null,
    BookStatus? Status = null,
    bool Unfinished = false,
    string? Author = null,
    string? Series = null,
    string? RecommendedBy = null,
    bool Recommended = false,
    BookFormat? Format = null,
    bool Loved = false,
    int? FromYear = null,
    int? ToYear = null);

public sealed record AskBook(int Id, string Title, string Author, BookStatus Status, string? Series, int? SeriesNumber, string? Cover, string Why);

public sealed record AskQuote(int BookId, string Title, string Text, string Kind);

public sealed record AskAnswer(string Question, string Understood, bool UsedAi, AskBook[] Books, AskQuote[] Quotes, string? Search);

// Questions about one's own shelf in everyday words, answered from the catalog and the reader's own notes with links
// to each book. The words are read here by simple rules; with an Anthropic key set, the question alone (never the
// shelf) may be read by the model to say what it asks, and the answer still comes only from the shelf.
public static partial class Concierge
{
    public const int MaxQuestionLength = 300;
    public const int MaxAnswers = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly HashSet<string> Small =
    [
        "a", "an", "the", "of", "about", "on", "in", "and", "or", "to", "for", "with", "which", "what", "who", "whom", "do", "does", "did", "i", "me", "my", "mine",
        "we", "our", "have", "has", "had", "own", "owned", "show", "find", "list", "give", "tell", "all", "any", "some", "that", "this", "these", "those", "is",
        "are", "was", "were", "be", "book", "books", "novel", "novels", "story", "stories", "read", "reading", "unread", "yet", "not", "never", "haven't", "havent",
        "i've", "ive", "there", "can", "could", "you", "please", "one", "ones", "something", "anything", "by", "from", "favorite", "favourite", "favorites",
        "favourites", "loved", "love", "quote", "quotes", "passage", "passages", "highlights", "highlight", "where", "when", "how", "many", "much", "still",
        "already", "finished", "started", "want", "wanted", "last", "this", "year", "years", "recommended", "recommend", "friend", "series", "audiobook",
        "audiobooks", "ebook", "ebooks", "e-book", "e-books", "paperback", "paperbacks", "hardcover", "hardcovers", "kind", "sort", "else", "lately", "recently",
    ];

    public static void Map(RouteGroupBuilder books) =>
        books.MapGet("/ask", async (string? q, ShelfDb db, IRecapWriter writer, IConfiguration configuration, CancellationToken cancellationToken) =>
            TypedResults.Ok(await AskAsync(db, q ?? "", AiAllowed(configuration) ? writer : null, cancellationToken: cancellationToken))).WithTags("Your reading");

    public static bool AiAllowed(IConfiguration configuration) => configuration.GetValue("Ask:UseModel", true);

    public static async Task<AskAnswer> AskAsync(ShelfDb db, string question, IRecapWriter? writer = null, DateOnly? today = null, CancellationToken cancellationToken = default)
    {
        var asked = question.Trim();
        asked = asked.Length > MaxQuestionLength ? asked[..MaxQuestionLength] : asked;
        var now = today ?? db.Today;
        var series = await db.Books.AsNoTracking().Where(book => book.Series != null).Select(book => book.Series!).Distinct().ToListAsync(cancellationToken);
        AskQuery? query = null;
        var usedAi = false;
        if (writer is { Ready: true } && asked.Length > 0)
        {
            query = await ReadWithModelAsync(writer, asked, now, cancellationToken);
            usedAi = query is not null;
        }

        query ??= Read(asked, now, series);
        var answer = await AnswerAsync(db, query, cancellationToken);
        return answer with { Question = asked, Understood = Describe(query), UsedAi = usedAi };
    }

    // The question read by rules: who, which series, when, what state, and the words left over as its subject.
    public static AskQuery Read(string question, DateOnly today, IReadOnlyCollection<string>? knownSeries = null)
    {
        var text = " " + question.ToLowerInvariant().Replace("’", "'", StringComparison.Ordinal) + " ";
        var looksFor = QuotePattern().IsMatch(text) ? AskLooksFor.Quotes : AskLooksFor.Books;
        BookStatus? status = null;
        var unfinished = false;
        if (UnreadPattern().IsMatch(text))
        {
            if (text.Contains(" series", StringComparison.Ordinal))
            {
                unfinished = true;
            }
            else
            {
                status = BookStatus.Want;
            }
        }
        else if (AbandonedPattern().IsMatch(text))
        {
            status = BookStatus.Abandoned;
        }
        else if (ReadingNowPattern().IsMatch(text))
        {
            status = BookStatus.Reading;
        }
        else if (FinishedPattern().IsMatch(text))
        {
            status = BookStatus.Finished;
        }

        string? author = null;
        if (AuthorPattern().Match(question) is { Success: true } by)
        {
            author = by.Groups["name"].Value.Trim().TrimEnd('?', '.', '!');
        }

        string? recommendedBy = null;
        var recommended = text.Contains("recommend", StringComparison.Ordinal);
        if (RecommenderPattern().Match(question) is { Success: true } who && !who.Groups["name"].Value.Trim().Equals("a friend", StringComparison.OrdinalIgnoreCase))
        {
            recommendedBy = who.Groups["name"].Value.Trim().TrimEnd('?', '.', '!');
            if (recommendedBy.StartsWith("my ", StringComparison.OrdinalIgnoreCase))
            {
                recommendedBy = null;
            }
        }

        // A series it names, by a series on the shelf, or "the … series".
        string? series = null;
        if (knownSeries is not null)
        {
            series = knownSeries.Where(name => name.Length >= 3 && text.Contains(" " + name.ToLowerInvariant(), StringComparison.Ordinal)).MaxBy(name => name.Length);
        }

        if (series is null && SeriesPattern().Match(question) is { Success: true } named)
        {
            series = named.Groups["name"].Value.Trim();
        }

        BookFormat? format = text switch
        {
            _ when text.Contains("audiobook", StringComparison.Ordinal) || text.Contains(" listen", StringComparison.Ordinal) => BookFormat.Audiobook,
            _ when text.Contains("e-book", StringComparison.Ordinal) || text.Contains("ebook", StringComparison.Ordinal) => BookFormat.Ebook,
            _ when text.Contains("paperback", StringComparison.Ordinal) => BookFormat.Paperback,
            _ when text.Contains("hardcover", StringComparison.Ordinal) || text.Contains("hardback", StringComparison.Ordinal) => BookFormat.Hardcover,
            _ => null,
        };
        var loved = FavouritePattern().IsMatch(text);

        int? from = null, to = null;
        if (text.Contains("last year", StringComparison.Ordinal))
        {
            (from, to) = (today.Year - 1, today.Year - 1);
        }
        else if (text.Contains("this year", StringComparison.Ordinal))
        {
            (from, to) = (today.Year, today.Year);
        }
        else if (YearPattern().Match(text) is { Success: true } year)
        {
            from = to = int.Parse(year.Groups["year"].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        // What is left is the subject.
        var rest = text;
        foreach (var taken in new[] { author, recommendedBy, series }.OfType<string>())
        {
            rest = rest.Replace(taken.ToLowerInvariant(), " ", StringComparison.Ordinal);
        }

        var words = WordPattern().Matches(rest).Select(match => match.Value.Trim('\'')).Where(word => word.Length >= 3 && !Small.Contains(word) && !int.TryParse(word, out _)).Distinct().ToArray();
        return new AskQuery(looksFor, words.Length == 0 ? null : words, status, unfinished, author, series, recommendedBy, recommended && recommendedBy is null, format, loved, from, to);
    }

    public static string Describe(AskQuery query)
    {
        var parts = new List<string> { query.LooksFor == AskLooksFor.Quotes ? T("quotes and highlights") : query.Status is BookStatus status ? T("books: {0}", BookRules.StatusLabel(status).ToLowerInvariant()) : T("books") };
        if (query.Unfinished)
        {
            parts.Add(T("not finished yet"));
        }

        if (query.Words is { Length: > 0 } words)
        {
            parts.Add(T("about {0}", string.Join(", ", words)));
        }

        if (query.Author is { } author)
        {
            parts.Add(T("by {0}", author));
        }

        if (query.Series is { } series)
        {
            parts.Add(T("in the series {0}", series));
        }

        if (query.RecommendedBy is { } who)
        {
            parts.Add(T("recommended by {0}", who));
        }
        else if (query.Recommended)
        {
            parts.Add(T("recommended by someone"));
        }

        if (query.Format is BookFormat format)
        {
            parts.Add(BookRules.FormatLabel(format).ToLowerInvariant());
        }

        if (query.Loved)
        {
            parts.Add(T("loved or rated highly"));
        }

        if (query.FromYear is int from)
        {
            parts.Add(query.ToYear is int to && to != from ? T("from {0} to {1}", from, to) : T("in {0}", from));
        }

        return string.Join(", ", parts);
    }

    public static async Task<AskAnswer> AnswerAsync(ShelfDb db, AskQuery query, CancellationToken cancellationToken = default)
    {
        var words = (query.Words ?? []).Select(word => word.ToLowerInvariant()).Select(Stem).Where(word => word.Length >= 3).Distinct().ToList();
        if (query.LooksFor == AskLooksFor.Quotes)
        {
            var quotes = await db.Quotes.AsNoTracking().Select(quote => new { quote.BookId, quote.Book!.Title, quote.Book.Author, quote.Book.Loved, quote.Book.Rating, quote.Text, Kind = "quote" })
                .Concat(db.Highlights.AsNoTracking().Select(mark => new { mark.BookId, mark.Book!.Title, mark.Book.Author, mark.Book.Loved, mark.Book.Rating, mark.Text, Kind = "highlight" }))
                .ToListAsync(cancellationToken);
            var found = quotes.Where(item => words.Count == 0 || words.Any(word => item.Text.Contains(word, StringComparison.OrdinalIgnoreCase)))
                .Where(item => query.Author is null || Tidy.AuthorKey(item.Author).Contains(Tidy.AuthorKey(query.Author), StringComparison.Ordinal))
                .OrderByDescending(item => words.Count(word => item.Text.Contains(word, StringComparison.OrdinalIgnoreCase)))
                .ThenByDescending(item => item.Loved || item.Rating >= 4)
                .Take(MaxAnswers)
                .Select(item => new AskQuote(item.BookId, item.Title, item.Text, item.Kind == "quote" ? T("Quote") : T("Highlight"))).ToArray();
            return new AskAnswer("", "", false, [], found, words.Count > 0 ? $"/search?q={Uri.EscapeDataString(string.Join(' ', query.Words ?? []))}&in=mine" : null);
        }

        var books = db.Books.AsNoTracking().Where(book => book.LeftOn == null);
        if (query.Status is BookStatus status)
        {
            books = books.Where(book => book.Status == status);
        }

        if (query.Unfinished)
        {
            books = books.Where(book => book.Status != BookStatus.Finished);
        }

        if (query.Format is BookFormat format)
        {
            books = books.Where(book => book.Format == format);
        }

        if (query.Series is { } series)
        {
            var name = series.ToLower();
            books = books.Where(book => book.Series != null && book.Series.ToLower().Contains(name));
        }

        if (query.RecommendedBy is { } who)
        {
            var name = who.ToLower();
            books = books.Where(book => book.RecommendedBy != null && book.RecommendedBy.ToLower().Contains(name));
        }
        else if (query.Recommended)
        {
            books = books.Where(book => book.RecommendedBy != null);
        }

        if (query.Loved)
        {
            books = books.Where(book => book.Loved || book.Rating >= 4);
        }

        if (query.FromYear is int from)
        {
            var (start, end) = (new DateOnly(from, 1, 1), new DateOnly(query.ToYear ?? from, 12, 31));
            books = query.Status == BookStatus.Finished ? books.Where(book => book.FinishedOn >= start && book.FinishedOn <= end) : books;
        }

        var rows = await books.Select(book => new
        {
            book.Id, book.Title, book.Subtitle, book.Author, book.Status, book.Series, book.SeriesNumber, book.Notes, book.Review, book.RecommendedBy,
            book.CoverUrl, book.FileCover, book.CoverImage, book.Publisher, book.AddedAt, book.FinishedOn, Tags = book.Tags.Select(tag => tag.Name).ToList(),
        }).ToListAsync(cancellationToken);

        // When it came or was finished, as moments can't be compared in SQLite.
        if (query.FromYear is int first && query.Status != BookStatus.Finished)
        {
            var last = query.ToYear ?? first;
            rows = [.. rows.Where(book => book.AddedAt.Year >= first && book.AddedAt.Year <= last || book.FinishedOn?.Year >= first && book.FinishedOn?.Year <= last)];
        }

        if (query.Author is { } author)
        {
            var key = Tidy.AuthorKey(author);
            rows = [.. rows.Where(book => Tidy.AuthorKey(book.Author).Contains(key, StringComparison.Ordinal) || key.Contains(Tidy.AuthorKey(book.Author), StringComparison.Ordinal))];
        }

        var scored = rows.Select(book =>
        {
            if (words.Count == 0)
            {
                return (Book: book, Score: 1, Why: query.RecommendedBy is not null || query.Recommended ? T("recommended by {0}", book.RecommendedBy ?? "") : "");
            }

            var why = new List<string>();
            var score = 0;
            foreach (var word in words)
            {
                if (book.Tags.Any(tag => tag.Contains(word, StringComparison.OrdinalIgnoreCase)))
                {
                    score += 3;
                    why.Add(T("tagged {0}", book.Tags.First(tag => tag.Contains(word, StringComparison.OrdinalIgnoreCase))));
                }
                else if (book.Title.Contains(word, StringComparison.OrdinalIgnoreCase) || (book.Subtitle?.Contains(word, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (book.Series?.Contains(word, StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    score += 2;
                    why.Add(T("“{0}” in the title", word));
                }
                else if ((book.Notes?.Contains(word, StringComparison.OrdinalIgnoreCase) ?? false) || (book.Review?.Contains(word, StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    score += 1;
                    why.Add(T("“{0}” in your notes", word));
                }
            }

            return (Book: book, Score: score, Why: string.Join(", ", why));
        }).Where(item => item.Score > 0).ToList();

        var best = scored.Count == 0 ? 0 : scored.Max(item => item.Score);
        var answer = scored.Where(item => words.Count <= 1 || item.Score >= Math.Min(best, words.Count * 2) || scored.Count <= MaxAnswers)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Book.Series ?? "￿", StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Book.SeriesNumber ?? int.MaxValue)
            .ThenBy(item => item.Book.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxAnswers)
            .Select(item => new AskBook(item.Book.Id, item.Book.Title, item.Book.Author, item.Book.Status, item.Book.Series, item.Book.SeriesNumber,
                Covers.For(item.Book.Id, item.Book.CoverUrl, item.Book.FileCover, item.Book.CoverImage), item.Why))
            .ToArray();
        return new AskAnswer("", "", false, answer, [], null);
    }

    private static string Stem(string word) =>
        word.EndsWith("ies", StringComparison.Ordinal) && word.Length > 4 ? word[..^3] + "y"
        : word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) && word.Length > 4 ? word[..^1]
        : word;

    // The question alone goes to the model, which answers with the query it asks for; nothing from the shelf is sent.
    private static async Task<AskQuery?> ReadWithModelAsync(IRecapWriter writer, string question, DateOnly today, CancellationToken cancellationToken)
    {
        const string system = """
            You turn a reader's question about their own book collection into a search, as one JSON object and nothing else.
            Fields (leave out what the question doesn't ask):
            "looksFor": "Books" or "Quotes"; "words": subject words in English and the question's own language (not names);
            "status": "Want" (unread), "Reading", "Finished", or "Abandoned"; "unfinished": true for books of a series not yet read;
            "author", "series", "recommendedBy": names as written; "recommended": true if it asks for books someone recommended;
            "format": "Hardcover", "Paperback", "Ebook", or "Audiobook"; "loved": true for favourites;
            "fromYear", "toYear": years the question names.
            """;
        var text = await writer.WriteAsync(system, $"Today is {today:yyyy-MM-dd}. The question: {question}", 300, cancellationToken);
        if (text is null)
        {
            return null;
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            var query = JsonSerializer.Deserialize<AskQuery>(text[start..(end + 1)], Json);
            return query is null ? null : query with
            {
                Words = query.Words?.Where(word => !string.IsNullOrWhiteSpace(word)).Select(word => word.Trim()).Take(8).ToArray(),
                Author = Clip(query.Author),
                Series = Clip(query.Series),
                RecommendedBy = Clip(query.RecommendedBy),
                Status = query.Status is BookStatus status && Enum.IsDefined(status) ? status : null,
                Format = query.Format is BookFormat format && Enum.IsDefined(format) ? format : null,
                FromYear = query.FromYear is >= 1000 and <= 2100 ? query.FromYear : null,
                ToYear = query.ToYear is >= 1000 and <= 2100 ? query.ToYear : null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Clip(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim()[..Math.Min(text.Trim().Length, 200)];

    [GeneratedRegex(@"\b(quotes?|passages?|highlights?|lines?)\b")]
    private static partial Regex QuotePattern();

    [GeneratedRegex(@"\b(unread|not (yet )?read|haven'?t read|never read|to read|on my list|not finished|yet to read)\b")]
    private static partial Regex UnreadPattern();

    [GeneratedRegex(@"\b(gave up|abandoned|didn'?t finish|did not finish|dnf)\b")]
    private static partial Regex AbandonedPattern();

    [GeneratedRegex(@"\b(reading now|currently reading|am reading|i'?m reading|in the middle of)\b")]
    private static partial Regex ReadingNowPattern();

    [GeneratedRegex(@"\b(have read|i read|i'?ve read|finished|already read)\b")]
    private static partial Regex FinishedPattern();

    [GeneratedRegex(@"\bby (?<name>[A-Z][\p{L}.'\-]*(?:\s+[A-Z][\p{L}.'\-]*){0,3})")]
    private static partial Regex AuthorPattern();

    [GeneratedRegex(@"\b(?<name>(?:[A-Z][\p{L}'\-]*)(?:\s+[A-Z][\p{L}'\-]*){0,2}) recommended\b|\brecommended by (?<name>[\p{L}'\-]+(?:\s+[A-Z][\p{L}'\-]*){0,2})")]
    private static partial Regex RecommenderPattern();

    [GeneratedRegex(@"\bthe (?<name>[\p{L}'\- ]{2,60}?) series\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesPattern();

    [GeneratedRegex(@"\b(favou?rites?|loved|best|top rated)\b")]
    private static partial Regex FavouritePattern();

    [GeneratedRegex(@"\b(in|from|during) (?<year>(1[89]|20)\d\d)\b")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"[\p{L}\p{N}'\-]+")]
    private static partial Regex WordPattern();
}

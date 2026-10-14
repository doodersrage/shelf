using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// A subject or author and how much of the reader's reading it is: books read, and their share of all read.
public sealed record DnaStrand(string Name, int Read, int Share, string Href);

public sealed record DnaYear(int Year, int Read, string[] Themes);

// A subject or author the reader owns many unread books of but has read few of.
public sealed record DnaGap(string Name, int Unread, int Read, string Href);

// A book on the shelf a long while, unread, that fits what the reader reads, with each reason it fits.
public sealed record DnaForgotten(int Id, string Title, string Author, string? Cover, DateTimeOffset AddedAt, string[] Reasons);

public sealed record ReadingDnaView(int Read, DnaStrand[] Themes, DnaStrand[] Authors, DnaYear[] Years, string[] Rising, string[] Fading, DnaGap[] Unexplored, DnaForgotten[] Forgotten);

// A picture of what a reader reads, made from their own shelf on the server when they ask for it, and kept nowhere:
// the subjects and authors they return to, how that has changed over the years, what they own but leave alone, and
// books long on the shelf that fit them, each with the reasons why. Nothing is sent anywhere.
public static class ReadingDna
{
    public const int Shown = 8;

    // Read is finished or being read; a book loved or rated highly counts for more, one rated low for less.
    private static double Weight(Book book) =>
        1 + (book.Loved ? 2 : 0) + (book.Rating >= 4 ? 1 : 0) - (book.Rating is <= 2 and > 0 ? 0.5 : 0);

    private static bool IsRead(Book book) => book.Status is BookStatus.Finished or BookStatus.Reading;

    public static async Task<ReadingDnaView> BuildAsync(ShelfDb db, DateOnly today, CancellationToken cancellationToken = default)
    {
        // Only what the picture needs; a big shelf's notes and chapter lists are slow to read.
        var books = await db.Books.AsNoTracking()
            .Select(book => new Book
            {
                Id = book.Id,
                Title = book.Title,
                Author = book.Author,
                Status = book.Status,
                Loved = book.Loved,
                Rating = book.Rating,
                Queued = book.Queued,
                Series = book.Series,
                SeriesNumber = book.SeriesNumber,
                AddedAt = book.AddedAt,
                StartedOn = book.StartedOn,
                FinishedOn = book.FinishedOn,
                CoverUrl = book.CoverUrl,
                FileCover = book.FileCover,
                CoverImage = book.CoverImage,
                Tags = book.Tags.ToList(),
            })
            .ToListAsync(cancellationToken);
        return Build(books, today);
    }

    public static ReadingDnaView Build(IReadOnlyList<Book> books, DateOnly today)
    {
        var read = books.Where(IsRead).ToList();
        var unread = books.Where(book => book.Status == BookStatus.Want).ToList();
        var themes = Strands(read, book => book.Tags.Select(tag => tag.Name), name => $"/library?tag={Uri.EscapeDataString(name)}");
        var authors = Strands(read, book => [book.Author], name => $"/library?author={Uri.EscapeDataString(name)}");

        // A year at a time, the last six with a few books finished.
        var years = read.Where(book => book.FinishedOn is not null).GroupBy(book => book.FinishedOn!.Value.Year)
            .Where(group => group.Count() >= 3).OrderByDescending(group => group.Key).Take(6)
            .Select(group => new DnaYear(group.Key, group.Count(), [.. Strands([.. group], book => book.Tags.Select(tag => tag.Name), _ => "").Take(3).Select(strand => strand.Name)]))
            .ToArray();

        // Rising and fading: a subject's share of the last two years' reading against its share before.
        var cut = today.AddYears(-2);
        var recent = read.Where(book => (book.FinishedOn ?? book.StartedOn) is DateOnly day && day >= cut).ToList();
        var earlier = read.Where(book => (book.FinishedOn ?? book.StartedOn) is DateOnly day && day < cut).ToList();
        var (rising, fading) = (new List<string>(), new List<string>());
        if (recent.Count >= 5 && earlier.Count >= 5)
        {
            var now = Shares(recent);
            var before = Shares(earlier);
            rising = [.. now.Where(pair => pair.Value.Count >= 3 && pair.Value.Share >= 1.5 * before.GetValueOrDefault(pair.Key).Share + 0.05).OrderByDescending(pair => pair.Value.Share).Take(5).Select(pair => pair.Key)];
            fading = [.. before.Where(pair => pair.Value.Count >= 3 && now.GetValueOrDefault(pair.Key).Share * 3 < pair.Value.Share).OrderByDescending(pair => pair.Value.Share).Take(5).Select(pair => pair.Key)];
        }

        // Owned and left alone: many unread with a subject or by an author, few read.
        var readTags = Count(read, book => book.Tags.Select(tag => tag.Name));
        var readAuthors = Count(read, book => [book.Author]);
        var unexplored = Count(unread, book => book.Tags.Select(tag => tag.Name))
            .Where(pair => pair.Value >= 3 && readTags.GetValueOrDefault(pair.Key) <= 1)
            .Select(pair => new DnaGap(pair.Key, pair.Value, readTags.GetValueOrDefault(pair.Key), $"/library?tag={Uri.EscapeDataString(pair.Key)}&status=Want"))
            .Concat(Count(unread, book => [book.Author])
                .Where(pair => pair.Value >= 3 && readAuthors.GetValueOrDefault(pair.Key) == 0)
                .Select(pair => new DnaGap(pair.Key, pair.Value, 0, $"/library?author={Uri.EscapeDataString(pair.Key)}&status=Want")))
            .OrderByDescending(gap => gap.Unread).ThenBy(gap => gap.Name, StringComparer.CurrentCultureIgnoreCase).Take(Shown).ToArray();

        return new ReadingDnaView(read.Count, [.. themes.Take(Shown)], [.. authors.Take(6)], years, [.. rising], [.. fading], unexplored, Forgotten(books, themes, authors, today));
    }

    // Books waiting a year or more, never begun, ranked by how well they fit; each says why.
    private static DnaForgotten[] Forgotten(IReadOnlyList<Book> books, IReadOnlyList<DnaStrand> themes, IReadOnlyList<DnaStrand> authors, DateOnly today)
    {
        var mine = themes.Take(Shown).Select(strand => strand.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var favourites = authors.Select(strand => strand.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loved = books.Where(book => book.Loved || book.Rating >= 4).Select(book => book.Author).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The highest number read in each series, to find the next one waiting.
        var readTo = books.Where(book => IsRead(book) && book.Series is not null && book.SeriesNumber is not null)
            .GroupBy(book => book.Series!, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Max(book => book.SeriesNumber!.Value), StringComparer.OrdinalIgnoreCase);
        var cutoff = new DateTimeOffset(today.AddYears(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var found = new List<(double Score, DnaForgotten Book)>();
        foreach (var book in books.Where(book => book.Status == BookStatus.Want && !book.Queued && book.StartedOn is null && book.AddedAt < cutoff))
        {
            var reasons = new List<string>();
            var score = 0.0;
            var shared = book.Tags.Select(tag => tag.Name).Where(mine.Contains).Take(2).ToList();
            if (shared.Count > 0)
            {
                score += 3 * shared.Count;
                reasons.Add(shared.Count == 1 ? T("About {0}, which you read often", shared[0]) : T("About {0} and {1}, which you read often", shared[0], shared[1]));
            }

            if (loved.Contains(book.Author))
            {
                score += 4;
                reasons.Add(T("By {0}, whose books you have loved", book.Author));
            }
            else if (favourites.Contains(book.Author))
            {
                score += 3;
                reasons.Add(T("By {0}, whom you read often", book.Author));
            }

            if (book.Series is { } series && book.SeriesNumber is int number && readTo.TryGetValue(series, out var last) && number == last + 1)
            {
                score += 4;
                reasons.Add(T("Next in {0}, after the one you read", series));
            }

            if (score == 0)
            {
                continue;
            }

            var years = (today.DayNumber - DateOnly.FromDateTime(book.AddedAt.UtcDateTime).DayNumber) / 365;
            score += Math.Min(years, 5) * 0.5;
            reasons.Add(years <= 1 ? T("On your shelf for over a year") : T("On your shelf for {0} years", years));
            found.Add((score, new DnaForgotten(book.Id, book.Title, book.Author, book.CoverShown, book.AddedAt, [.. reasons])));
        }

        return [.. found.OrderByDescending(item => item.Score).ThenBy(item => item.Book.AddedAt).Take(10).Select(item => item.Book)];
    }

    private static List<DnaStrand> Strands(IReadOnlyList<Book> read, Func<Book, IEnumerable<string>> names, Func<string, string> href)
    {
        var weights = new Dictionary<string, (double Weight, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var book in read)
        {
            foreach (var name in names(book).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var (weight, count) = weights.GetValueOrDefault(name);
                weights[name] = (weight + Weight(book), count + 1);
            }
        }

        return [.. weights.Where(pair => pair.Value.Count >= 2).OrderByDescending(pair => pair.Value.Weight).ThenBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(pair => new DnaStrand(pair.Key, pair.Value.Count, read.Count == 0 ? 0 : (int)Math.Round(100.0 * pair.Value.Count / read.Count), href(pair.Key)))];
    }

    private static Dictionary<string, (int Count, double Share)> Shares(IReadOnlyList<Book> read) =>
        Count(read, book => book.Tags.Select(tag => tag.Name)).ToDictionary(pair => pair.Key, pair => (pair.Value, (double)pair.Value / read.Count), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, int> Count(IEnumerable<Book> books, Func<Book, IEnumerable<string>> names)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in books.SelectMany(book => names(book).Distinct(StringComparer.OrdinalIgnoreCase)))
        {
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }

        return counts;
    }

    public static void Map(RouteGroupBuilder books) =>
        books.MapGet("/dna", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await BuildAsync(db, DateOnly.FromDateTime(DateTime.UtcNow), cancellationToken)));
}

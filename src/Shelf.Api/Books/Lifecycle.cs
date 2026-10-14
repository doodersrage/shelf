using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public sealed record LetGoRequest(Departure How, DateOnly? On = null, string? To = null, decimal? SoldFor = null);

// One year of the collection: copies that came, were finished, and left, and what they cost and brought.
public sealed record LifecycleYear(int Year, int Came, int Finished, int Left, Dictionary<Departure, int> LeftHow, decimal Spent, decimal Got, int OwnedAtEnd);

public sealed record AgeBucket(string Label, int Books);

public sealed record CountedName(string Name, int Books);

public sealed record LifecycleBook(int Id, string Title, string Author, DateTimeOffset AddedAt, int Times);

public sealed record LifecycleReport(
    LifecycleYear[] Years,
    int Owned,
    int Unread,
    AgeBucket[] UnreadByAge,
    CountedName[] UnreadByFormat,
    CountedName[] UnreadByTag,
    CountedName[] UnreadBySeries,
    LifecycleBook[] Reread,
    int NeverOpened,
    LifecycleBook[] LongestWaiting,
    decimal Spent,
    decimal Got,
    int Priced);

// How a library changes: what comes in, is read, and goes, beside what is read. Copies let go keep their record.
public static class Lifecycle
{
    public static void Map(RouteGroupBuilder books)
    {
        books.MapGet("/lifecycle", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await ReportAsync(db, cancellationToken: cancellationToken))).WithTags("Your reading");
        books.MapPost("/{id:int}/let-go", async Task<IResult> (int id, LetGoRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            await LetGoAsync(db, id, request, cancellationToken) switch
            {
                null => TypedResults.NoContent(),
                var problem when problem == Missing => TypedResults.NotFound(),
                var problem => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["How"] = [problem] }),
            }).WithTags("Books");
        books.MapDelete("/{id:int}/let-go", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await TakeBackAsync(db, id, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound()).WithTags("Books");
    }

    private static readonly string Missing = "\0";

    public static string DepartureLabel(Departure how) => how switch
    {
        Departure.Donated => T("Donated"),
        Departure.Sold => T("Sold"),
        Departure.Given => T("Given away"),
        Departure.Discarded => T("Discarded"),
        _ => T("Lost"),
    };

    public static string Describe(Book book)
    {
        var how = book.LeftHow is Departure left ? DepartureLabel(left) : T("Let go");
        var parts = new List<string> { book.LeftOn is DateOnly on ? T("{0} on {1}", how, on.Medium()) : how };
        if (book.LeftTo is { } to)
        {
            parts.Add(T("to {0}", to));
        }

        if (book.SoldFor is decimal price)
        {
            parts.Add(T("for {0}", Money.Show(price)));
        }

        return string.Join(" ", parts);
    }

    // A copy let go: kept in the catalog, with its reading, out of the way of the library and the queue.
    public static async Task<string?> LetGoAsync(ShelfDb db, int id, LetGoRequest request, CancellationToken cancellationToken = default)
    {
        var book = await db.Books.FirstOrDefaultAsync(item => item.Id == id && item.VisitingLinkId == null, cancellationToken);
        if (book is null)
        {
            return Missing;
        }

        if (!Enum.IsDefined(request.How))
        {
            return T("Choose how it left.");
        }

        if (book.BorrowerId is not null || book.LoanedTo is not null)
        {
            return T("The book is on loan. Mark it returned first.");
        }

        if (request.SoldFor is < 0 or > 1000000)
        {
            return T("Give a price from 0 on.");
        }

        var on = request.On ?? DateOnly.FromDateTime(DateTime.UtcNow);
        if (on > DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1))
        {
            return T("Give a day that has come.");
        }

        var to = string.IsNullOrWhiteSpace(request.To) ? null : request.To.Trim();
        (book.LeftOn, book.LeftHow, book.LeftTo) = (on, request.How, to is { Length: > 120 } ? to[..120] : to);
        book.SoldFor = request.How == Departure.Sold && request.SoldFor is decimal price ? Math.Round(price, 2) : null;
        (book.Queued, book.QueueOrder, book.HeldUntil, book.HoldNote) = (false, null, null, null);
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    public static async Task<bool> TakeBackAsync(ShelfDb db, int id, CancellationToken cancellationToken = default)
    {
        var book = await db.Books.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (book is null)
        {
            return false;
        }

        (book.LeftOn, book.LeftHow, book.LeftTo, book.SoldFor) = (null, null, null, null);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Worked out from the shelf as it is, when asked. A copy came when it was acquired, or else when it was added.
    public static async Task<LifecycleReport> ReportAsync(ShelfDb db, DateOnly? today = null, CancellationToken cancellationToken = default)
    {
        var now = today ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var books = await db.Books.AsNoTracking().Where(book => book.VisitingLinkId == null)
            .Select(book => new
            {
                book.Id, book.Title, book.Author, book.Status, book.Format, book.Series, book.AcquiredOn, book.AddedAt, book.LeftOn, book.LeftHow,
                book.PricePaid, book.SoldFor, book.CurrentPage, book.EbookProgress, book.AudioSeconds, book.AudioTrack, book.StartedOn,
            })
            .ToListAsync(cancellationToken);
        var finishes = await Rereads.FinishesAsync(db, cancellationToken: cancellationToken);
        var logged = (await db.Sessions.AsNoTracking().Select(session => session.BookId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        var reads = finishes.GroupBy(finish => finish.BookId).ToDictionary(group => group.Key, group => group.Count());

        int CameIn(DateOnly? acquired, DateTimeOffset added) => (acquired ?? DateOnly.FromDateTime(added.UtcDateTime)).Year;
        var firstYear = books.Count == 0 ? now.Year : Math.Min(books.Min(book => CameIn(book.AcquiredOn, book.AddedAt)), finishes.Count == 0 ? now.Year : finishes.Min(finish => finish.FinishedOn.Year));
        firstYear = Math.Max(firstYear, now.Year - 60);
        var years = new List<LifecycleYear>();
        for (var year = firstYear; year <= now.Year; year++)
        {
            var came = books.Where(book => CameIn(book.AcquiredOn, book.AddedAt) == year).ToList();
            var left = books.Where(book => book.LeftOn?.Year == year).ToList();
            var owned = books.Count(book => CameIn(book.AcquiredOn, book.AddedAt) <= year && (book.LeftOn is null || book.LeftOn.Value.Year > year));
            years.Add(new LifecycleYear(year, came.Count, finishes.Count(finish => finish.FinishedOn.Year == year), left.Count,
                left.Where(book => book.LeftHow is not null).GroupBy(book => book.LeftHow!.Value).ToDictionary(group => group.Key, group => group.Count()),
                came.Sum(book => book.PricePaid ?? 0), left.Sum(book => book.SoldFor ?? 0), owned));
        }

        var kept = books.Where(book => book.LeftOn is null).ToList();
        var unread = kept.Where(book => book.Status == BookStatus.Want).ToList();
        var tags = (await db.Books.AsNoTracking().Where(book => book.Status == BookStatus.Want && book.LeftOn == null && book.VisitingLinkId == null)
            .SelectMany(book => book.Tags.Select(tag => tag.Name)).ToListAsync(cancellationToken))
            .GroupBy(name => name).Select(group => new CountedName(group.Key, group.Count())).OrderByDescending(item => item.Books).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).Take(10).ToArray();
        static int Age(DateOnly since, DateOnly now) => now.Year - since.Year - (now.DayOfYear < since.DayOfYear ? 1 : 0);
        var ages = unread.Select(book => Age(DateOnly.FromDateTime(book.AddedAt.UtcDateTime), now)).ToList();
        var buckets = new[]
        {
            new AgeBucket(T("Under a year"), ages.Count(age => age < 1)),
            new AgeBucket(T("1–2 years"), ages.Count(age => age is >= 1 and < 3)),
            new AgeBucket(T("3–5 years"), ages.Count(age => age is >= 3 and < 6)),
            new AgeBucket(T("6–10 years"), ages.Count(age => age is >= 6 and < 11)),
            new AgeBucket(T("Over 10 years"), ages.Count(age => age >= 11)),
        };

        // Never opened: waiting two years or more with nothing read, heard, or logged.
        var cutoff = new DateTimeOffset(now.AddYears(-2).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var untouched = unread.Where(book => book.AddedAt < cutoff && book.StartedOn is null && (book.CurrentPage ?? 0) == 0 && (book.EbookProgress ?? 0) == 0
            && (book.AudioSeconds ?? 0) == 0 && (book.AudioTrack ?? 0) == 0 && !logged.Contains(book.Id)).ToList();
        var titles = books.ToDictionary(book => book.Id);
        return new LifecycleReport(
            [.. years.OrderByDescending(year => year.Year)],
            kept.Count,
            unread.Count,
            buckets,
            [.. unread.GroupBy(book => book.Format).Select(group => new CountedName(group.Key is BookFormat format ? BookRules.FormatLabel(format) : T("Format not noted"), group.Count())).OrderByDescending(item => item.Books)],
            tags,
            [.. unread.Where(book => book.Series is not null).GroupBy(book => book.Series!, StringComparer.OrdinalIgnoreCase).Select(group => new CountedName(group.First().Series!, group.Count()))
                .OrderByDescending(item => item.Books).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).Take(10)],
            [.. reads.Where(pair => pair.Value > 1 && titles.ContainsKey(pair.Key)).OrderByDescending(pair => pair.Value).Take(20)
                .Select(pair => new LifecycleBook(pair.Key, titles[pair.Key].Title, titles[pair.Key].Author, titles[pair.Key].AddedAt, pair.Value))],
            untouched.Count,
            [.. untouched.OrderBy(book => book.AddedAt).Take(20).Select(book => new LifecycleBook(book.Id, book.Title, book.Author, book.AddedAt, 0))],
            books.Sum(book => book.PricePaid ?? 0),
            books.Sum(book => book.SoldFor ?? 0),
            books.Count(book => book.PricePaid is not null));
    }
}

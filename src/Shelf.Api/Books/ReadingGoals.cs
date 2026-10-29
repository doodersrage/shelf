using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// What a goal of a year counts: books with a tag, by an author, in a series, or by authors new to the reader.
public enum GoalKind
{
    Tag,
    Author,
    Series,
    NewAuthors,

    // Books finished that were already on the shelf when the year began.
    Backlog,

    // Copies let go in the year: donated, sold, given away.
    LetGo,
}

// A goal of the reader's own for a year, beside the books, pages, and hours: three books of history, two by Ursula
// K. Le Guin, the rest of Discworld, five authors never read before. Rereads count, as they do toward the year's books.
public sealed class ReadingGoal
{
    public const int MaxNameLength = 200;
    public const int MaxPerYear = 20;

    public int Id { get; set; }
    public int OwnerId { get; set; }
    public int Year { get; set; }
    public GoalKind Kind { get; set; }

    // The tag, author, or series; none for new authors.
    public string? Name { get; set; }
    public int Target { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record ReadingGoalRequest(GoalKind Kind, [MaxLength(ReadingGoal.MaxNameLength)] string? Name, [Range(1, 1000)] int Target, int? Year = null);

// How a goal stands: the books that count so far, and a gentle word on what would see it done.
public sealed record GoalProgress(int Id, int Year, GoalKind Kind, string? Name, int Target, int Done, string[] Books, string Label, string? Pace);

public static class ReadingGoals
{
    public static void Map(RouteGroupBuilder books)
    {
        var goals = books.MapGroup("/goals");
        goals.MapGet("/", async (int? year, ShelfDb db, CancellationToken cancellationToken) =>
            TypedResults.Ok(await ProgressAsync(db, year ?? DateTime.UtcNow.Year, cancellationToken: cancellationToken)));
        goals.MapPost("/", async Task<IResult> (ReadingGoalRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            await AddAsync(db, request, cancellationToken) switch
            {
                (int id, null) => TypedResults.Created($"/books/goals/{id}", new { id }),
                (_, var problem) => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Name"] = [problem!] }),
            });
        goals.MapDelete("/{id:int}", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await db.ReadingGoals.Where(goal => goal.Id == id).ExecuteDeleteAsync(cancellationToken) > 0 ? TypedResults.NoContent() : TypedResults.NotFound());
    }

    public static async Task<(int? Id, string? Problem)> AddAsync(ShelfDb db, ReadingGoalRequest request, CancellationToken cancellationToken = default)
    {
        var year = request.Year ?? DateTime.UtcNow.Year;
        var name = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim();
        if (!Enum.IsDefined(request.Kind) || year is < 1000 or > 2100)
        {
            return (null, T("Choose what the goal counts."));
        }

        if (request.Target is < 1 or > 1000)
        {
            return (null, T("Aim for between 1 and 1,000 books."));
        }

        if (request.Kind is GoalKind.NewAuthors or GoalKind.Backlog or GoalKind.LetGo)
        {
            name = null;
        }
        else if (name is null)
        {
            return (null, T("Name the tag, author, or series."));
        }
        else if (name.Length > ReadingGoal.MaxNameLength)
        {
            return (null, T("Keep the name to {0} characters.", ReadingGoal.MaxNameLength));
        }

        if (request.Kind == GoalKind.Tag)
        {
            name = name!.ToLowerInvariant();
        }

        if (await db.ReadingGoals.CountAsync(goal => goal.Year == year, cancellationToken) >= ReadingGoal.MaxPerYear)
        {
            return (null, T("A year can have up to {0} goals of this kind.", ReadingGoal.MaxPerYear));
        }

        var made = new ReadingGoal { OwnerId = db.ReaderId, Year = year, Kind = request.Kind, Name = name, Target = request.Target, CreatedAt = DateTimeOffset.UtcNow };
        db.ReadingGoals.Add(made);
        await db.SaveChangesAsync(cancellationToken);
        return (made.Id, null);
    }

    // The year's goals and how each stands. Only the year's finishes are read in full; for new authors, the authors
    // finished before it are read as names alone.
    public static async Task<GoalProgress[]> ProgressAsync(ShelfDb db, int year, DateOnly? today = null, CancellationToken cancellationToken = default)
    {
        var goals = await db.ReadingGoals.AsNoTracking().Where(goal => goal.Year == year).OrderBy(goal => goal.Id).ToListAsync(cancellationToken);
        if (goals.Count == 0)
        {
            return [];
        }

        var finishes = (await Rereads.FinishesAsync(db, year, cancellationToken)).OrderBy(finish => finish.FinishedOn).ToList();
        var ids = finishes.Select(finish => finish.BookId).Distinct().ToList();
        var details = await db.Books.AsNoTracking().Where(book => ids.Contains(book.Id))
            .Select(book => new { book.Id, book.Series, book.AddedAt, Tags = book.Tags.Select(tag => tag.Name).ToList() })
            .ToDictionaryAsync(book => book.Id, cancellationToken);

        HashSet<string>? before = null;
        if (goals.Any(goal => goal.Kind == GoalKind.NewAuthors))
        {
            var start = new DateOnly(year, 1, 1);
            before = (await db.Books.AsNoTracking().Where(book => book.Status == BookStatus.Finished && book.FinishedOn < start).Select(book => book.Author).ToListAsync(cancellationToken))
                .Concat(await db.PastReads.AsNoTracking().Where(read => read.FinishedOn < start).Select(read => read.Book!.Author).ToListAsync(cancellationToken))
                .Select(Key).ToHashSet();
        }

        var yearStart = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var letGo = goals.Any(goal => goal.Kind == GoalKind.LetGo)
            ? await db.Books.AsNoTracking().Where(book => book.LeftOn >= new DateOnly(year, 1, 1) && book.LeftOn <= new DateOnly(year, 12, 31))
                .OrderBy(book => book.LeftOn).Select(book => book.Title).ToListAsync(cancellationToken)
            : [];
        var now = today ?? db.Today;
        return goals.Select(goal =>
        {
            List<string> counted;
            if (goal.Kind == GoalKind.LetGo)
            {
                counted = letGo;
            }
            else if (goal.Kind == GoalKind.Backlog)
            {
                counted = finishes.Where(finish => details.TryGetValue(finish.BookId, out var book) && book.AddedAt < yearStart)
                    .DistinctBy(finish => finish.BookId).Select(finish => finish.Title).ToList();
            }
            else if (goal.Kind == GoalKind.NewAuthors)
            {
                // Each new author once, by the first of their books finished in the year.
                counted = finishes.Where(finish => !before!.Contains(Key(finish.Author)))
                    .DistinctBy(finish => Key(finish.Author)).Select(finish => finish.Author).ToList();
            }
            else
            {
                counted = finishes.Where(finish => goal.Kind switch
                {
                    GoalKind.Tag => details.TryGetValue(finish.BookId, out var book) && book.Tags.Contains(goal.Name!, StringComparer.OrdinalIgnoreCase),
                    GoalKind.Author => Key(finish.Author) == Key(goal.Name!),
                    _ => details.TryGetValue(finish.BookId, out var book) && book.Series is not null && Key(book.Series) == Key(goal.Name!),
                }).Select(finish => finish.Title).ToList();
            }

            return new GoalProgress(goal.Id, goal.Year, goal.Kind, goal.Name, goal.Target, counted.Count, [.. counted], Label(goal.Kind, goal.Name, goal.Target), Pace(goal.Target - counted.Count, goal.Year, now));
        }).ToArray();
    }

    public static string Label(GoalKind kind, string? name, int target) => kind switch
    {
        GoalKind.Tag => target == 1 ? T("1 book tagged {0}", name ?? "") : T("{0} books tagged {1}", target, name ?? ""),
        GoalKind.Author => target == 1 ? T("1 book by {0}", name ?? "") : T("{0} books by {1}", target, name ?? ""),
        GoalKind.Series => target == 1 ? T("1 book in {0}", name ?? "") : T("{0} books in {1}", target, name ?? ""),
        GoalKind.Backlog => target == 1 ? T("1 book from before the year began") : T("{0} books from before the year began", target),
        GoalKind.LetGo => target == 1 ? T("1 copy let go") : T("{0} copies let go", target),
        _ => target == 1 ? T("1 author new to you") : T("{0} authors new to you", target),
    };

    // Not behind or ahead: only what would see it done in the time left, said kindly. Null once done, or for a year gone.
    public static string? Pace(int left, int year, DateOnly today)
    {
        if (left <= 0 || today.Year > year)
        {
            return null;
        }

        var end = new DateOnly(year, 12, 31);
        var start = today.Year < year ? new DateOnly(year, 1, 1) : today;
        var weeks = Math.Max(1, (end.DayNumber - start.DayNumber + 1) / 7);
        if (left >= weeks)
        {
            return T("{0} to go in {1} weeks; any of them is a good read whether or not it counts.", left, weeks);
        }

        var every = weeks / left;
        return left == 1
            ? T("1 to go, with {0} weeks left: no hurry.", weeks)
            : T("{0} to go: about one every {1} weeks would do it.", left, every);
    }

    private static string Key(string text) => text.Trim().ToLowerInvariant();
}

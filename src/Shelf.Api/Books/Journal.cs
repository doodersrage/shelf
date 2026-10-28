using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// A few lines a reader writes for a day: about a book, or about where they were in life while reading.
public sealed class JournalEntry
{
    public const int MaxTextLength = 4000;

    public int Id { get; set; }
    public int OwnerId { get; set; }
    public DateOnly Date { get; set; }
    public required string Text { get; set; }
    public int? BookId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum JournalKind
{
    Entry,
    Started,
    Finished,
    Session,
    Highlight,
    Quote,
    Thoughts,
}

// One line of the journal, from what the reader wrote or what the shelf kept as they read.
public sealed record JournalItem(DateOnly Date, JournalKind Kind, int? BookId, string? Title, string? Text, string? Detail = null, int? EntryId = null, int? Rating = null);

public sealed record JournalEntryRequest(DateOnly? Date, [Required, MaxLength(JournalEntry.MaxTextLength)] string Text, int? BookId = null);

public static class Journal
{
    public const int MaxFound = 200;

    public static void Map(RouteGroupBuilder books)
    {
        var journal = books.MapGroup("/journal");
        journal.MapGet("/", async (int? year, string? q, ShelfDb db, CancellationToken cancellationToken) =>
            TypedResults.Ok(string.IsNullOrWhiteSpace(q)
                ? await YearAsync(db, year ?? DateTime.UtcNow.Year, cancellationToken)
                : await SearchAsync(db, q, cancellationToken)));
        journal.MapGet("/years", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await YearsAsync(db, cancellationToken)));
        journal.MapPost("/", async Task<IResult> (JournalEntryRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            await WriteAsync(db, null, request.Date, request.Text, request.BookId, cancellationToken) switch
            {
                (int id, null) => TypedResults.Created($"/books/journal/{id}", new { id }),
                (_, { } problem) => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Text"] = [problem] }),
                _ => TypedResults.NotFound(),
            });
        journal.MapPut("/{id:int}", async Task<IResult> (int id, JournalEntryRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            await WriteAsync(db, id, request.Date, request.Text, request.BookId, cancellationToken) switch
            {
                (int, null) => TypedResults.NoContent(),
                (_, { } problem) => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Text"] = [problem] }),
                _ => TypedResults.NotFound(),
            });
        journal.MapDelete("/{id:int}", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await db.JournalEntries.Where(entry => entry.Id == id).ExecuteDeleteAsync(cancellationToken) > 0 ? TypedResults.NoContent() : TypedResults.NotFound());
        journal.MapGet("/{year:int}.md", async (int year, ShelfDb db, CancellationToken cancellationToken) =>
            Results.File(Encoding.UTF8.GetBytes(Markdown(year, await YearAsync(db, year, cancellationToken))), "text/markdown; charset=utf-8", $"reading-journal-{year}.md"));
    }

    // A new entry (id null) or a change to one; the entry's id, or a problem to tell. Neither when the entry or
    // its book is not the reader's.
    public static async Task<(int? Id, string? Problem)> WriteAsync(ShelfDb db, int? id, DateOnly? date, string? text, int? bookId, CancellationToken cancellationToken = default)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return (null, T("Write something first."));
        }

        if (text.Length > JournalEntry.MaxTextLength)
        {
            return (null, T("Keep it to {0} characters.", JournalEntry.MaxTextLength));
        }

        if (bookId is int book && !await db.Books.AnyAsync(item => item.Id == book, cancellationToken))
        {
            return (null, null);
        }

        var entry = id is int existing
            ? await db.JournalEntries.FirstOrDefaultAsync(item => item.Id == existing, cancellationToken)
            : new JournalEntry { OwnerId = db.ReaderId, Text = text, CreatedAt = DateTimeOffset.UtcNow };
        if (entry is null)
        {
            return (null, null);
        }

        entry.Date = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        entry.Text = text;
        entry.BookId = bookId;
        if (id is null)
        {
            db.JournalEntries.Add(entry);
        }

        await db.SaveChangesAsync(cancellationToken);
        return (entry.Id, null);
    }

    // The years with anything in them, latest first, and this year always.
    public static async Task<int[]> YearsAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var years = new HashSet<int> { DateTime.UtcNow.Year };
        years.UnionWith(await db.JournalEntries.AsNoTracking().Select(entry => entry.Date.Year).Distinct().ToListAsync(cancellationToken));
        years.UnionWith(await db.Sessions.AsNoTracking().Select(session => session.Date.Year).Distinct().ToListAsync(cancellationToken));
        years.UnionWith(await db.Books.AsNoTracking().Where(book => book.FinishedOn != null).Select(book => book.FinishedOn!.Value.Year).Distinct().ToListAsync(cancellationToken));
        years.UnionWith(await db.PastReads.AsNoTracking().Where(read => read.FinishedOn != null).Select(read => read.FinishedOn!.Value.Year).Distinct().ToListAsync(cancellationToken));
        return [.. years.OrderDescending()];
    }

    // A year of reading, latest first: what the reader wrote, the books begun and finished, the sessions logged, and
    // the passages kept. Each part is read for the year alone, so a long life of reading opens as quickly as a short one.
    public static async Task<JournalItem[]> YearAsync(ShelfDb db, int year, CancellationToken cancellationToken = default)
    {
        var (from, to) = (new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
        var (start, end) = (new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        var items = new List<JournalItem>();

        var entries = await db.JournalEntries.AsNoTracking().Where(entry => entry.Date >= from && entry.Date <= to)
            .Select(entry => new { entry.Id, entry.Date, entry.Text, entry.BookId }).ToListAsync(cancellationToken);
        var titles = await TitlesAsync(db, entries.Where(entry => entry.BookId != null).Select(entry => entry.BookId!.Value), cancellationToken);
        items.AddRange(entries.Select(entry => new JournalItem(entry.Date, JournalKind.Entry, entry.BookId, entry.BookId is int id ? titles.GetValueOrDefault(id) : null, entry.Text, EntryId: entry.Id)));

        // A start after the finish was stamped by older Shelfs on a book added as finished, and is no start at all.
        items.AddRange(await db.Books.AsNoTracking().Where(book => book.StartedOn >= from && book.StartedOn <= to && (book.FinishedOn == null || book.StartedOn <= book.FinishedOn))
            .Select(book => new JournalItem(book.StartedOn!.Value, JournalKind.Started, book.Id, book.Title, null, book.Author, null, null)).ToListAsync(cancellationToken));
        items.AddRange(await db.PastReads.AsNoTracking().Where(read => read.StartedOn >= from && read.StartedOn <= to)
            .Select(read => new JournalItem(read.StartedOn!.Value, JournalKind.Started, read.BookId, read.Book!.Title, null, read.Book.Author, null, null)).ToListAsync(cancellationToken));
        items.AddRange((await Rereads.FinishesAsync(db, year, cancellationToken))
            .Select(finish => new JournalItem(finish.FinishedOn, JournalKind.Finished, finish.BookId, finish.Title, null, finish.Author, null, finish.Rating)));
        items.AddRange(await db.PastReads.AsNoTracking().Where(read => read.Thoughts != null && read.FinishedOn >= from && read.FinishedOn <= to)
            .Select(read => new JournalItem(read.FinishedOn!.Value, JournalKind.Thoughts, read.BookId, read.Book!.Title, read.Thoughts, null, null, null)).ToListAsync(cancellationToken));

        var sessions = await db.Sessions.AsNoTracking().Where(session => session.Date >= from && session.Date <= to)
            .Select(session => new { session.Date, session.BookId, session.Book!.Title, session.FromPage, session.ToPage, session.Note }).ToListAsync(cancellationToken);
        items.AddRange(sessions.Select(session => new JournalItem(session.Date, JournalKind.Session, session.BookId, session.Title, session.Note, Pages(session.FromPage, session.ToPage))));

        // SQLite can't compare the times kept as text, so which passages fall in the year is told here, from one read
        // of them each. Not by listing the year's ids back to the database: a long-kept shelf has tens of thousands,
        // and a query naming each one took over a minute to build.
        items.AddRange((await db.Highlights.AsNoTracking()
            .Select(mark => new { mark.NotedAt, mark.BookId, mark.Book!.Title, mark.Text, mark.Note }).ToListAsync(cancellationToken))
            .Where(mark => mark.NotedAt >= start && mark.NotedAt < end)
            .Select(mark => new JournalItem(Day(mark.NotedAt), JournalKind.Highlight, mark.BookId, mark.Title, mark.Text, mark.Note)));
        items.AddRange((await db.Quotes.AsNoTracking()
            .Select(quote => new { quote.NotedAt, quote.BookId, quote.Book!.Title, quote.Text, quote.Page }).ToListAsync(cancellationToken))
            .Where(quote => quote.NotedAt >= start && quote.NotedAt < end)
            .Select(quote => new JournalItem(Day(quote.NotedAt), JournalKind.Quote, quote.BookId, quote.Title, quote.Text, quote.Page is int page ? T("page {0}", page) : null)));

        return [.. Ordered(items)];
    }

    // Everything written across every year that holds the words: entries, session notes, highlights and their notes,
    // quotes, and thoughts on reads gone by.
    public static async Task<JournalItem[]> SearchAsync(ShelfDb db, string text, CancellationToken cancellationToken = default)
    {
        var term = text.Trim().ToLower();
        var items = new List<JournalItem>();
        var entries = await db.JournalEntries.AsNoTracking().Where(entry => entry.Text.ToLower().Contains(term))
            .OrderByDescending(entry => entry.Date).Take(MaxFound).Select(entry => new { entry.Id, entry.Date, entry.Text, entry.BookId }).ToListAsync(cancellationToken);
        var titles = await TitlesAsync(db, entries.Where(entry => entry.BookId != null).Select(entry => entry.BookId!.Value), cancellationToken);
        items.AddRange(entries.Select(entry => new JournalItem(entry.Date, JournalKind.Entry, entry.BookId, entry.BookId is int id ? titles.GetValueOrDefault(id) : null, entry.Text, EntryId: entry.Id)));
        items.AddRange(await db.Sessions.AsNoTracking().Where(session => session.Note != null && session.Note.ToLower().Contains(term))
            .OrderByDescending(session => session.Date).Take(MaxFound)
            .Select(session => new JournalItem(session.Date, JournalKind.Session, session.BookId, session.Book!.Title, session.Note, null, null, null)).ToListAsync(cancellationToken));
        items.AddRange(await db.PastReads.AsNoTracking().Where(read => read.Thoughts != null && read.Thoughts.ToLower().Contains(term))
            .Take(MaxFound)
            .Select(read => new JournalItem(read.FinishedOn ?? read.StartedOn ?? DateOnly.MinValue, JournalKind.Thoughts, read.BookId, read.Book!.Title, read.Thoughts, null, null, null)).ToListAsync(cancellationToken));
        items.AddRange((await db.Highlights.AsNoTracking().Where(mark => mark.Text.ToLower().Contains(term) || (mark.Note != null && mark.Note.ToLower().Contains(term)))
            .OrderByDescending(mark => mark.Id).Take(MaxFound)
            .Select(mark => new { mark.NotedAt, mark.BookId, mark.Book!.Title, mark.Text, mark.Note }).ToListAsync(cancellationToken))
            .Select(mark => new JournalItem(Day(mark.NotedAt), JournalKind.Highlight, mark.BookId, mark.Title, mark.Text, mark.Note)));
        items.AddRange((await db.Quotes.AsNoTracking().Where(quote => quote.Text.ToLower().Contains(term))
            .OrderByDescending(quote => quote.Id).Take(MaxFound)
            .Select(quote => new { quote.NotedAt, quote.BookId, quote.Book!.Title, quote.Text, quote.Page }).ToListAsync(cancellationToken))
            .Select(quote => new JournalItem(Day(quote.NotedAt), JournalKind.Quote, quote.BookId, quote.Title, quote.Text, quote.Page is int page ? T("page {0}", page) : null)));
        return [.. Ordered(items).Take(MaxFound)];
    }

    // The year as Markdown, month by month, for a notes folder or to print.
    public static string Markdown(int year, IReadOnlyList<JournalItem> items)
    {
        var text = new StringBuilder();
        text.Append("# ").AppendLine(T("Reading journal, {0}", year)).AppendLine();
        foreach (var month in items.OrderBy(item => item.Date).GroupBy(item => item.Date.Month))
        {
            text.Append("## ").AppendLine(new DateOnly(year, month.Key, 1).MonthYear()).AppendLine();
            foreach (var item in month)
            {
                text.Append("- **").Append(item.Date.Medium()).Append("** ").AppendLine(Line(item));
                if (item.Kind is JournalKind.Entry or JournalKind.Highlight or JournalKind.Quote or JournalKind.Thoughts or JournalKind.Session && !string.IsNullOrWhiteSpace(item.Text))
                {
                    foreach (var line in item.Text!.Split('\n'))
                    {
                        text.Append("  > ").AppendLine(line.TrimEnd());
                    }
                }

                if (item.Kind == JournalKind.Highlight && !string.IsNullOrWhiteSpace(item.Detail))
                {
                    text.Append("  ").AppendLine(item.Detail);
                }
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    // What the line says happened, without the words it carries.
    public static string Line(JournalItem item) => item.Kind switch
    {
        JournalKind.Entry when item.Title is not null => T("Wrote, on {0}", item.Title),
        JournalKind.Entry => T("Wrote"),
        JournalKind.Started => T("Started {0}", item.Title ?? ""),
        JournalKind.Finished when item.Rating is int stars => T("Finished {0}", item.Title ?? "") + " · " + new string('★', stars),
        JournalKind.Finished => T("Finished {0}", item.Title ?? ""),
        JournalKind.Session when item.Detail is not null => T("Read {0}, {1}", item.Title ?? "", item.Detail),
        JournalKind.Session => T("Read {0}", item.Title ?? ""),
        JournalKind.Highlight => T("Highlighted in {0}", item.Title ?? ""),
        JournalKind.Quote when item.Detail is not null => T("Kept a quote from {0}, {1}", item.Title ?? "", item.Detail),
        JournalKind.Quote => T("Kept a quote from {0}", item.Title ?? ""),
        _ => T("Thoughts on reading {0} before", item.Title ?? ""),
    };

    private static IEnumerable<JournalItem> Ordered(IEnumerable<JournalItem> items) =>
        items.OrderByDescending(item => item.Date).ThenBy(item => item.Kind switch
        {
            JournalKind.Finished => 0,
            JournalKind.Thoughts => 1,
            JournalKind.Entry => 2,
            JournalKind.Started => 6,
            _ => 3,
        });

    private static string? Pages(int? from, int? to) =>
        (from, to) switch
        {
            (int start, int end) => T("pages {0}–{1}", start, end),
            (null, int end) => T("to page {0}", end),
            _ => null,
        };

    private static async Task<Dictionary<int, string>> TitlesAsync(ShelfDb db, IEnumerable<int> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.Distinct().ToList();
        return wanted.Count == 0
            ? []
            : await db.Books.AsNoTracking().Where(book => wanted.Contains(book.Id)).ToDictionaryAsync(book => book.Id, book => book.Title, cancellationToken);
    }

    private static DateOnly Day(DateTimeOffset at) => DateOnly.FromDateTime(at.UtcDateTime);
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// What one change to many books was: an edit to a selection, books deleted together, an import, a scan, or details
// filled in. Each can be undone as a whole.
public enum BatchKind
{
    Edit,
    Delete,
    Import,
    Scan,
    Details,
    Undo,
}

// One change to many books, under which its BookChange rows (and, for a deletion, its TrashItem rows) are gathered.
public sealed class ChangeBatch
{
    public const int MaxSummaryLength = 300;

    public int Id { get; set; }
    public int OwnerId { get; set; }
    public BatchKind Kind { get; set; }
    public required string Summary { get; set; }
    public DateTimeOffset At { get; set; }
    public DateTimeOffset? UndoneAt { get; set; }
}

// One field of one book changed: what it was, and what it became, each as JSON. A tag put on or taken off is
// Field "Tag" with the tag's name after or before; a book a batch brought is Field "Added".
public sealed class BookChange
{
    public const string Added = "Added";
    public const string Tag = "Tag";

    public long Id { get; set; }
    public int OwnerId { get; set; }

    // Null once the book itself has gone; put back if it comes back from the trash.
    public int? BookId { get; set; }
    public Book? Book { get; set; }
    public int? BatchId { get; set; }
    public DateTimeOffset At { get; set; }
    public required string Field { get; set; }
    public string? Before { get; set; }
    public string? After { get; set; }
}

public sealed record BookChangeItem(long Id, DateTimeOffset At, string Field, string Label, string? Before, string? After, int? BatchId, string? Batch);

public sealed record BatchItem(int Id, BatchKind Kind, string Summary, DateTimeOffset At, DateTimeOffset? UndoneAt, int Books, bool CanUndo);

public sealed record UndoResult(int Books);

// A book's history: every change to its catalog details, kept a year (History:Days), and changes to many books
// gathered so each can be undone at once.
public static class History
{
    // The book's fields a reader writes, whose changes are kept. Places, progress, and files are left out: they
    // change all the time, and have their own ways back.
    public static readonly IReadOnlyList<string> Fields =
    [
        nameof(Book.Title), nameof(Book.Subtitle), nameof(Book.Author), nameof(Book.Isbn), nameof(Book.Year), nameof(Book.Pages),
        nameof(Book.Publisher), nameof(Book.Language), nameof(Book.Format), nameof(Book.Series), nameof(Book.SeriesNumber),
        nameof(Book.CoverUrl), nameof(Book.Location), nameof(Book.Status), nameof(Book.Rating), nameof(Book.Loved), nameof(Book.Notes),
        nameof(Book.Review), nameof(Book.StartedOn), nameof(Book.FinishedOn), nameof(Book.Translator), nameof(Book.Narrator),
        nameof(Book.OriginalTitle), nameof(Book.Inscription), nameof(Book.RecommendedBy), nameof(Book.PreviousOwners), nameof(Book.Signed),
        nameof(Book.Condition), nameof(Book.Acquisition), nameof(Book.AcquiredOn), nameof(Book.PricePaid), nameof(Book.LeftOn), nameof(Book.LeftHow),
        nameof(Book.LeftTo), nameof(Book.SoldFor), nameof(Book.HeldUntil), nameof(Book.HoldNote), nameof(Book.HomeLocation),
    ];

    private static readonly HashSet<string> Kept = [.. Fields];

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly AsyncLocal<int?> Current = new();

    // The batch that changes saved from here on belong to, until the result is disposed. Set it after the batch
    // is saved, in the method that goes on to make the changes, so the work it awaits carries it along.
    public static IDisposable Use(int? batchId)
    {
        var before = Current.Value;
        Current.Value = batchId;
        return new Restore(() => Current.Value = before);
    }

    public static int? CurrentBatch => Current.Value;

    public static async Task<ChangeBatch> StartAsync(ShelfDb db, BatchKind kind, string summary, CancellationToken cancellationToken = default)
    {
        var batch = new ChangeBatch
        {
            OwnerId = db.ReaderId,
            Kind = kind,
            Summary = summary.Length > ChangeBatch.MaxSummaryLength ? summary[..ChangeBatch.MaxSummaryLength] : summary,
            At = DateTimeOffset.UtcNow,
        };
        db.ChangeBatches.Add(batch);
        await db.SaveChangesAsync(cancellationToken);
        return batch;
    }

    // Called as a save begins: a row for each kept field of a book that changed, each tag on or off, and each book
    // a batch brings.
    public static void Note(ShelfDb db)
    {
        var tracker = db.ChangeTracker;
        var at = DateTimeOffset.UtcNow;
        var batch = Current.Value;
        var books = tracker.Entries<Book>().ToList();
        foreach (var entry in books)
        {
            var book = entry.Entity;
            if (book.OwnerId is not int owner)
            {
                continue;
            }

            if (entry.State == EntityState.Added)
            {
                if (batch is not null)
                {
                    db.BookChanges.Add(new BookChange { OwnerId = owner, Book = book, BatchId = batch, At = at, Field = BookChange.Added });
                }

                continue;
            }

            if (entry.State != EntityState.Modified)
            {
                continue;
            }

            foreach (var property in entry.Properties)
            {
                var name = property.Metadata.Name;
                if (!property.IsModified || !Kept.Contains(name) || Equals(property.OriginalValue, property.CurrentValue))
                {
                    continue;
                }

                if (property.OriginalValue is string was && property.CurrentValue is string now && was.Trim() == now.Trim())
                {
                    continue;
                }

                db.BookChanges.Add(new BookChange
                {
                    OwnerId = owner, BookId = book.Id, BatchId = batch, At = at, Field = name,
                    Before = Write(property.OriginalValue), After = Write(property.CurrentValue),
                });
            }
        }

        // Tags on and off, through the join between books and tags.
        foreach (var join in tracker.Entries<Dictionary<string, object>>().Where(item => item.Metadata.GetTableName() == "BookTags").ToList())
        {
            if (join.State is not (EntityState.Added or EntityState.Deleted))
            {
                continue;
            }

            var bookEntry = books.FirstOrDefault(item => Equals(item.Property(book => book.Id).CurrentValue, join.Property("BooksId").CurrentValue));
            if (bookEntry is null || bookEntry.State == EntityState.Added || bookEntry.Entity.OwnerId is not int owner)
            {
                continue;
            }

            var tagId = join.Property("TagsId").CurrentValue;
            var name = tracker.Entries<Tag>().FirstOrDefault(item => Equals(item.Property(tag => tag.Id).CurrentValue, tagId))?.Entity.Name;
            if (name is null)
            {
                continue;
            }

            var added = join.State == EntityState.Added;
            db.BookChanges.Add(new BookChange
            {
                OwnerId = owner, BookId = bookEntry.Entity.Id, BatchId = batch, At = at, Field = BookChange.Tag,
                Before = added ? null : Write(name), After = added ? Write(name) : null,
            });
        }
    }

    public static string? Write(object? value) => value is null ? null : JsonSerializer.Serialize(value, JsonOptions);

    // A kept value as the reader reads it: a date, a status, or the text itself.
    public static string? Show(string field, string? json)
    {
        if (json is null)
        {
            return null;
        }

        var type = field is BookChange.Tag or BookChange.Added ? typeof(string) : PropertyType(field);
        if (type is null)
        {
            return json;
        }

        var value = JsonSerializer.Deserialize(json, type, JsonOptions);
        return value switch
        {
            null => null,
            BookStatus status => BookRules.StatusLabel(status),
            BookFormat format => BookRules.FormatLabel(format),
            bool yes => yes ? T("yes") : T("no"),
            DateOnly date => date.Medium(),
            _ => value.ToString(),
        };
    }

    public static string Label(string field) => field switch
    {
        nameof(Book.Title) => T("Title"),
        nameof(Book.Subtitle) => T("Subtitle"),
        nameof(Book.Author) => T("Author"),
        nameof(Book.Isbn) => "ISBN",
        nameof(Book.Year) => T("Year"),
        nameof(Book.Pages) => T("Pages"),
        nameof(Book.Publisher) => T("Publisher"),
        nameof(Book.Language) => T("Language"),
        nameof(Book.Format) => T("Format"),
        nameof(Book.Series) => T("Series"),
        nameof(Book.SeriesNumber) => T("Number in series"),
        nameof(Book.CoverUrl) => T("Cover address"),
        nameof(Book.Location) => T("Place"),
        nameof(Book.Status) => T("Status"),
        nameof(Book.Rating) => T("Rating"),
        nameof(Book.Loved) => T("Loved"),
        nameof(Book.Notes) => T("Notes"),
        nameof(Book.Review) => T("Review"),
        nameof(Book.StartedOn) => T("Started"),
        nameof(Book.FinishedOn) => T("Finished"),
        nameof(Book.Translator) => T("Translator"),
        nameof(Book.Narrator) => T("Narrator"),
        nameof(Book.OriginalTitle) => T("Original title"),
        nameof(Book.Inscription) => T("Inscription"),
        nameof(Book.RecommendedBy) => T("Recommended by"),
        nameof(Book.PreviousOwners) => T("Earlier owners"),
        nameof(Book.Signed) => T("Signed"),
        nameof(Book.Condition) => T("Condition"),
        nameof(Book.Acquisition) => T("How it came"),
        nameof(Book.AcquiredOn) => T("Acquired"),
        nameof(Book.PricePaid) => T("Price paid"),
        nameof(Book.LeftOn) => T("Let go"),
        nameof(Book.LeftHow) => T("How it left"),
        nameof(Book.LeftTo) => T("Went to"),
        nameof(Book.SoldFor) => T("Sold for"),
        nameof(Book.HeldUntil) => T("Held until"),
        nameof(Book.HoldNote) => T("Held for"),
        nameof(Book.HomeLocation) => T("Usual place"),
        BookChange.Tag => T("Tag"),
        BookChange.Added => T("Added"),
        _ => field,
    };

    public static string Describe(BatchKind kind) => kind switch
    {
        BatchKind.Edit => T("Changed together"),
        BatchKind.Delete => T("Deleted together"),
        BatchKind.Import => T("Brought in"),
        BatchKind.Scan => T("Scanned in"),
        BatchKind.Details => T("Details filled in"),
        _ => T("Undone"),
    };

    private static Type? PropertyType(string field) =>
        typeof(Book).GetProperty(field)?.PropertyType is { } type ? Nullable.GetUnderlyingType(type) ?? type : null;

    private static object? Read(string field, string? json) =>
        json is null || typeof(Book).GetProperty(field)?.PropertyType is not { } type ? null : JsonSerializer.Deserialize(json, type, JsonOptions);

    public static async Task<BookChangeItem[]> ForBookAsync(ShelfDb db, int bookId, CancellationToken cancellationToken = default)
    {
        var rows = await db.BookChanges.AsNoTracking()
            .Where(change => change.BookId == bookId && change.Field != BookChange.Added)
            .OrderByDescending(change => change.Id)
            .Take(200)
            .ToListAsync(cancellationToken);
        var batchIds = rows.Where(row => row.BatchId != null).Select(row => row.BatchId!.Value).Distinct().ToList();
        var batches = await db.ChangeBatches.AsNoTracking().Where(batch => batchIds.Contains(batch.Id) && batch.OwnerId == db.ReaderId)
            .ToDictionaryAsync(batch => batch.Id, batch => batch.Summary, cancellationToken);
        return rows.Select(row => new BookChangeItem(row.Id, row.At, row.Field, Label(row.Field), Show(row.Field, row.Before), Show(row.Field, row.After), row.BatchId,
            row.BatchId is int id ? batches.GetValueOrDefault(id) : null)).ToArray();
    }

    // Puts one field of one book back as it was before the change, whatever it is now.
    public static async Task<bool> PutBackAsync(ShelfDb db, int bookId, long changeId, CancellationToken cancellationToken = default)
    {
        var change = await db.BookChanges.AsNoTracking().FirstOrDefaultAsync(item => item.Id == changeId && item.BookId == bookId, cancellationToken);
        var book = await db.Books.Include(item => item.Tags).FirstOrDefaultAsync(item => item.Id == bookId, cancellationToken);
        if (change is null || book is null || change.Field == BookChange.Added)
        {
            return false;
        }

        await ApplyAsync(db, book, change.Field, change.Before, change.After, force: true, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await BookRules.RemoveUnusedTagsAsync(db, cancellationToken);
        return true;
    }

    // Sets a field back from after to before. Unless forced, only where the field is still as the change left it,
    // so a later edit by hand is never undone.
    private static async Task<bool> ApplyAsync(ShelfDb db, Book book, string field, string? before, string? after, bool force, CancellationToken cancellationToken)
    {
        if (field == BookChange.Tag)
        {
            var name = before is null ? null : JsonSerializer.Deserialize<string>(before, JsonOptions);
            var gone = after is null ? null : JsonSerializer.Deserialize<string>(after, JsonOptions);
            if (gone is not null)
            {
                return book.Tags.RemoveAll(tag => tag.Name == gone) > 0;
            }

            if (name is not null && book.Tags.All(tag => tag.Name != name) && book.Tags.Count < BookRules.MaxTags)
            {
                book.Tags.Add(await db.Tags.FirstOrDefaultAsync(tag => tag.Name == name, cancellationToken)
                    ?? db.Tags.Local.FirstOrDefault(tag => tag.Name == name)
                    ?? db.Tags.Add(new Tag { Name = name }).Entity);
                return true;
            }

            return false;
        }

        var property = db.Entry(book).Property(field);
        if (!Kept.Contains(field))
        {
            return false;
        }

        if (!force && !Equals(property.CurrentValue, Read(field, after)))
        {
            return false;
        }

        property.CurrentValue = Read(field, before);
        return true;
    }

    public static async Task<BatchItem[]> BatchesAsync(ShelfDb db, int take = 50, CancellationToken cancellationToken = default)
    {
        var me = db.ReaderId;
        var batches = await db.ChangeBatches.AsNoTracking().Where(batch => batch.OwnerId == me)
            .OrderByDescending(batch => batch.Id).Take(take).ToListAsync(cancellationToken);
        var ids = batches.Select(batch => batch.Id).ToList();
        var changed = await db.BookChanges.AsNoTracking().Where(change => change.BatchId != null && ids.Contains(change.BatchId.Value))
            .GroupBy(change => change.BatchId!.Value)
            .Select(group => new { group.Key, Books = group.Select(change => change.BookId).Distinct().Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Books, cancellationToken);
        var trashed = await db.TrashItems.AsNoTracking().Where(item => item.BatchId != null && ids.Contains(item.BatchId.Value))
            .GroupBy(item => item.BatchId!.Value)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, cancellationToken);
        return batches
            .Select(batch => (batch, books: batch.Kind == BatchKind.Delete ? trashed.GetValueOrDefault(batch.Id) : changed.GetValueOrDefault(batch.Id)))
            .Where(pair => pair.books > 0 || pair.batch.Kind == BatchKind.Delete && pair.batch.UndoneAt is not null)
            .Select(pair => new BatchItem(pair.batch.Id, pair.batch.Kind, pair.batch.Summary, pair.batch.At, pair.batch.UndoneAt, pair.books,
                pair.batch.UndoneAt is null && pair.batch.Kind != BatchKind.Undo && pair.books > 0))
            .ToArray();
    }

    // Undoes a whole batch: edits set back where the books are still as it left them, books it brought sent to the
    // trash, books it deleted brought back.
    public static async Task<UndoResult?> UndoAsync(ShelfDb db, EbookStore ebooks, AudioStore audio, CoverStore covers, int batchId, CancellationToken cancellationToken = default)
    {
        var batch = await db.ChangeBatches.AsNoTracking().FirstOrDefaultAsync(item => item.Id == batchId && item.OwnerId == db.ReaderId, cancellationToken);
        if (batch is null || batch.UndoneAt is not null || batch.Kind == BatchKind.Undo)
        {
            return null;
        }

        var count = 0;
        if (batch.Kind == BatchKind.Delete)
        {
            var items = await db.TrashItems.Where(item => item.BatchId == batchId).Select(item => item.Id).ToListAsync(cancellationToken);
            count = (await Trash.RestoreManyAsync(db, items, cancellationToken)).Values.Count(result => result.Restored);
        }
        else
        {
            var changes = await db.BookChanges.AsNoTracking().Where(change => change.BatchId == batchId && change.BookId != null)
                .OrderByDescending(change => change.Id).ToListAsync(cancellationToken);
            var added = changes.Where(change => change.Field == BookChange.Added).Select(change => change.BookId!.Value).Distinct().ToList();
            var undo = await StartAsync(db, BatchKind.Undo, T("Undid: {0}", batch.Summary), cancellationToken);
            using (Use(undo.Id))
            {
                var bookIds = changes.Where(change => change.Field != BookChange.Added).Select(change => change.BookId!.Value).Distinct().Except(added).ToList();
                foreach (var chunk in bookIds.Chunk(500))
                {
                    var books = await db.Books.Include(book => book.Tags).Where(book => chunk.Contains(book.Id)).ToDictionaryAsync(book => book.Id, cancellationToken);
                    foreach (var change in changes.Where(change => change.Field != BookChange.Added && chunk.Contains(change.BookId!.Value)))
                    {
                        if (books.TryGetValue(change.BookId!.Value, out var book) && await ApplyAsync(db, book, change.Field, change.Before, change.After, force: false, cancellationToken))
                        {
                            count++;
                        }
                    }

                    await db.SaveChangesAsync(cancellationToken);
                }

                if (added.Count > 0)
                {
                    count += await Trash.DeleteBooksAsync(db, ebooks, audio, covers, added, undo.Id, cancellationToken);
                }
            }

            await BookRules.RemoveUnusedTagsAsync(db, cancellationToken);
        }

        var now = DateTimeOffset.UtcNow;
        await db.ChangeBatches.Where(item => item.Id == batchId).ExecuteUpdateAsync(set => set.SetProperty(item => item.UndoneAt, now), cancellationToken);
        return new UndoResult(count);
    }

    // Changes older than History:Days go, and batches with them, but for a batch whose deleted books the trash keeps.
    public static async Task<int> TrimAsync(ShelfDb db, int days, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, days));
        var gone = 0;
        if (await LastBeforeAsync(db.BookChanges.IgnoreQueryFilters().AsNoTracking().Select(change => new Stamp { Id = change.Id, At = change.At }), cutoff, cancellationToken) is long change)
        {
            gone = await db.BookChanges.IgnoreQueryFilters().Where(item => item.Id <= change).ExecuteDeleteAsync(cancellationToken);
        }

        if (await LastBeforeAsync(db.ChangeBatches.AsNoTracking().Select(batch => new Stamp { Id = batch.Id, At = batch.At }), cutoff, cancellationToken) is long batch)
        {
            var last = (int)batch;
            await db.ChangeBatches.Where(item => item.Id <= last && !db.TrashItems.IgnoreQueryFilters().Any(trashed => trashed.BatchId == item.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        return gone;
    }

    public sealed class Stamp
    {
        public long Id { get; init; }
        public DateTimeOffset At { get; init; }
    }

    // The highest id among rows written before the cutoff, for rows written in time order. Read a page at a time from
    // the oldest, as SQLite cannot compare the times itself.
    public static async Task<long?> LastBeforeAsync(IQueryable<Stamp> rows, DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        long? last = null;
        while (true)
        {
            var after = last ?? long.MinValue;
            var page = await rows.Where(row => row.Id > after).OrderBy(row => row.Id).Take(1000).ToListAsync(cancellationToken);
            foreach (var row in page)
            {
                if (row.At >= cutoff)
                {
                    return last;
                }

                last = row.Id;
            }

            if (page.Count < 1000)
            {
                return last;
            }
        }
    }

    public static void Map(RouteGroupBuilder books)
    {
        books.MapGet("/{id:int}/history", async Task<Results<Ok<BookChangeItem[]>, NotFound>> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await db.Books.AnyAsync(book => book.Id == id, cancellationToken) ? TypedResults.Ok(await ForBookAsync(db, id, cancellationToken)) : TypedResults.NotFound());
        books.MapPost("/{id:int}/history/{changeId:long}/undo", async Task<Results<NoContent, NotFound>> (int id, long changeId, ShelfDb db, CancellationToken cancellationToken) =>
            await PutBackAsync(db, id, changeId, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());
        books.MapGet("/changes", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await BatchesAsync(db, cancellationToken: cancellationToken)));
        books.MapPost("/changes/{id:int}/undo", async Task<Results<Ok<UndoResult>, NotFound>> (int id, ShelfDb db, EbookStore ebooks, AudioStore audio, CoverStore covers, CancellationToken cancellationToken) =>
            await UndoAsync(db, ebooks, audio, covers, id, cancellationToken) is { } result ? TypedResults.Ok(result) : TypedResults.NotFound());
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}

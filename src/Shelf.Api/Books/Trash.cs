using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public enum TrashKind
{
    Book,
    Quote,
    Session,
    Highlight,
}

// Something deleted, kept a while (Trash:Days, 30 by default) so it can be brought back: the rows as they were, as
// JSON, and for a book the files it had, which stay where they are until the trash lets them go.
public sealed class TrashItem
{
    public const int MaxTitleLength = 200;
    public const int MaxDetailLength = 300;

    public int Id { get; set; }
    public int OwnerId { get; set; }
    public TrashKind Kind { get; set; }

    // The book it was, or was on.
    public int? BookId { get; set; }
    public required string Title { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset DeletedAt { get; set; }

    // Deleted along with others, and brought back with them by undoing it.
    public int? BatchId { get; set; }
    public required string Payload { get; set; }

    // The stored files to delete when it goes for good, a line each: "e:", "a:", "c:", or "p:" and the stored name.
    public string? Files { get; set; }

    // A book merged into another: what the merge moved onto that one (a MergeReceipt as JSON), taken back when this
    // is restored, so a merge made by mistake comes apart again.
    public string? MergeReceipt { get; set; }
}

// The rows of one table, each as its properties' values.
public sealed record TrashRows(string Entity, Dictionary<string, JsonElement>[] Rows);

public sealed record TrashPayload(TrashRows[] Tables, string[] Tags);

public sealed record TrashView(int Id, TrashKind Kind, string Title, string? Detail, DateTimeOffset DeletedAt, int DaysLeft, int? BookId);

public sealed record TrashRestore(bool Restored, int? BookId, string? Problem);

public static class Trash
{
    public const int DefaultDays = 30;

    // Kept by the books they belong to but made again from the file when wanted, or a download under way: not kept.
    private static readonly HashSet<Type> Remade = [typeof(OcrScan), typeof(SpokenText), typeof(ReadAlongRow), typeof(PartSummary), typeof(FetchGrab)];

    public static void Map(RouteGroupBuilder books)
    {
        var trash = books.MapGroup("/trash");
        trash.MapGet("/", async (ShelfDb db, IConfiguration configuration, CancellationToken cancellationToken) =>
            TypedResults.Ok(await ListAsync(db, Days(configuration), cancellationToken)));
        trash.MapPost("/{id:int}/restore", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await RestoreAsync(db, id, cancellationToken) switch
            {
                null => TypedResults.NotFound(),
                { Restored: true } restored => TypedResults.Ok(restored),
                var problem => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Id"] = [problem.Problem ?? ""] }),
            });
        trash.MapDelete("/{id:int}", async Task<Results<NoContent, NotFound>> (int id, ShelfDb db, EbookStore ebooks, AudioStore audio, CoverStore covers, CancellationToken cancellationToken) =>
            await ForgetAsync(db, ebooks, audio, covers, db.TrashItems.Where(item => item.Id == id), cancellationToken) > 0 ? TypedResults.NoContent() : TypedResults.NotFound());
        trash.MapDelete("/", async (ShelfDb db, EbookStore ebooks, AudioStore audio, CoverStore covers, CancellationToken cancellationToken) =>
            TypedResults.Ok(new { Removed = await ForgetAsync(db, ebooks, audio, covers, db.TrashItems, cancellationToken) }));
    }

    public static int Days(IConfiguration configuration) => Math.Clamp(configuration.GetValue("Trash:Days", DefaultDays), 1, 3650);

    public static async Task<TrashView[]> ListAsync(ShelfDb db, int days, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var items = await db.TrashItems.AsNoTracking().OrderByDescending(item => item.Id)
            .Select(item => new { item.Id, item.Kind, item.Title, item.Detail, item.DeletedAt, item.BookId })
            .ToListAsync(cancellationToken);
        return items.Select(item => new TrashView(item.Id, item.Kind, item.Title, item.Detail, item.DeletedAt,
            Math.Max(0, (int)Math.Ceiling((item.DeletedAt.AddDays(days) - now).TotalDays)), item.BookId)).ToArray();
    }

    // Deletes the reader's books, each kept in the trash with all that is theirs. Their files stay where they are.
    public static async Task<int> DeleteBooksAsync(ShelfDb db, EbookStore ebooks, AudioStore audio, CoverStore covers, IReadOnlyCollection<int> ids, int? batchId = null, CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var chunk in ids.Distinct().Chunk(100))
        {
            var captured = await CaptureBooksAsync(db, chunk, cancellationToken);
            var photos = (await db.CopyPhotos.AsNoTracking().Where(photo => chunk.Contains(photo.BookId)).Select(photo => new { photo.BookId, photo.StoredName }).ToListAsync(cancellationToken))
                .ToLookup(photo => photo.BookId, photo => photo.StoredName);
            var formats = (await db.EbookFormats.AsNoTracking().Where(format => chunk.Contains(format.BookId)).Select(format => new { format.BookId, format.StoredName }).ToListAsync(cancellationToken))
                .ToLookup(format => format.BookId, format => format.StoredName);
            foreach (var (id, (payload, book)) in captured)
            {
                var files = new List<string>();
                if (book.EbookStoredName is { } ebook) files.Add("e:" + ebook);
                files.AddRange(formats[id].Select(format => "e:" + format));
                if (book.AudioStoredName is { } recording) files.Add("a:" + recording);
                if (book.CoverImage is { } cover) files.Add("c:" + cover);
                files.AddRange(photos[id].Select(photo => "p:" + photo));
                db.TrashItems.Add(new TrashItem
                {
                    OwnerId = book.OwnerId ?? db.ReaderId,
                    Kind = TrashKind.Book,
                    BookId = id,
                    Title = Cut(book.Title, TrashItem.MaxTitleLength),
                    Detail = Cut(book.Author, TrashItem.MaxDetailLength),
                    DeletedAt = DateTimeOffset.UtcNow,
                    BatchId = batchId,
                    Payload = JsonSerializer.Serialize(payload, History.JsonOptions),
                    Files = files.Count == 0 ? null : string.Join('\n', files),
                });
            }

            var keys = captured.Keys.ToList();
            db.Books.RemoveRange(await db.Books.Where(book => keys.Contains(book.Id)).ToListAsync(cancellationToken));
            await db.SaveChangesAsync(cancellationToken);
            count += captured.Count;
        }

        await BookRules.RemoveUnusedTagsAsync(db, cancellationToken);
        return count;
    }

    // A quote, a session, or a highlight, deleted and kept.
    public static async Task DeleteRowAsync<T>(ShelfDb db, T row, TrashKind kind, int bookId, string title, string? detail, CancellationToken cancellationToken = default)
        where T : class
    {
        var type = db.Model.FindEntityType(typeof(T))!;
        var payload = new TrashPayload([new TrashRows(type.Name, [Write(type, row)])], []);
        db.TrashItems.Add(new TrashItem
        {
            OwnerId = db.ReaderId,
            Kind = kind,
            BookId = bookId,
            Title = Cut(title, TrashItem.MaxTitleLength),
            Detail = detail is null ? null : Cut(detail, TrashItem.MaxDetailLength),
            DeletedAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.Serialize(payload, History.JsonOptions),
        });
        db.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
    }

    // The books and every row that goes with each, read from the model, so a table added later comes along by itself.
    // Rows that only point at a book (a journal entry, a change) are listed to be pointed at it again. Only the
    // reader's own books are read.
    public static async Task<Dictionary<int, (TrashPayload Payload, Book Book)>> CaptureBooksAsync(ShelfDb db, IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default)
    {
        var bookType = db.Model.FindEntityType(typeof(Book))!;
        var books = await db.Books.AsNoTracking().Include(item => item.Tags).Where(item => ids.Contains(item.Id)).ToListAsync(cancellationToken);
        var found = books.Select(book => book.Id).ToList();
        var tables = books.ToDictionary(book => book.Id, book => new List<TrashRows> { new(bookType.Name, [Write(bookType, book)]) });
        foreach (var group in bookType.GetReferencingForeignKeys().GroupBy(key => key.DeclaringEntityType))
        {
            var type = group.Key;
            if (type.HasSharedClrType || Remade.Contains(type.ClrType) || type.ClrType == typeof(Book))
            {
                continue;
            }

            var rows = found.ToDictionary(id => id, _ => new Dictionary<string, Dictionary<string, JsonElement>>());
            foreach (var key in group)
            {
                var property = key.Properties.Single();
                foreach (var row in await RowsAsync(db, type.ClrType, property.Name, property.ClrType, found, cancellationToken))
                {
                    var written = Write(type, row);
                    var owner = Convert.ToInt32(property.PropertyInfo!.GetValue(row));
                    rows[owner].TryAdd(string.Join('|', type.FindPrimaryKey()!.Properties.Select(item => written.GetValueOrDefault(item.Name).ToString())), written);
                }
            }

            foreach (var (id, kept) in rows.Where(pair => pair.Value.Count > 0))
            {
                tables[id].Add(new TrashRows(type.Name, [.. kept.Values]));
            }
        }

        return books.ToDictionary(book => book.Id, book => (new TrashPayload([.. tables[book.Id]], [.. book.Tags.Select(tag => tag.Name)]), book));
    }

    // Brings an item back as it was. A book keeps its id when no other book has taken it, so devices and links that
    // knew it still find it; anything it pointed at that has gone since (a reader, a collection) is left out.
    public static async Task<TrashRestore?> RestoreAsync(ShelfDb db, int id, CancellationToken cancellationToken = default) =>
        (await RestoreManyAsync(db, [id], cancellationToken)).GetValueOrDefault(id);

    // Several items brought back in one go, as undoing a deletion of many books does: one transaction, and what is
    // known to be there or gone found out once for all of them.
    public static async Task<Dictionary<int, TrashRestore>> RestoreManyAsync(ShelfDb db, IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<int, TrashRestore>();
        var known = new Dictionary<(Type, object), bool>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var chunk in ids.Chunk(100))
        {
            var items = await db.TrashItems.AsNoTracking().Where(row => chunk.Contains(row.Id)).ToListAsync(cancellationToken);
            foreach (var item in items)
            {
                var payload = JsonSerializer.Deserialize<TrashPayload>(item.Payload, History.JsonOptions)!;
                results[item.Id] = item.Kind == TrashKind.Book
                    ? await RestoreBookAsync(db, payload, item.OwnerId, null, cancellationToken, known)
                    : await RestoreRowAsync(db, payload, item.BookId, cancellationToken);
                db.ChangeTracker.Clear();
                if (item.MergeReceipt is { } kept && results[item.Id] is { Restored: true, BookId: int restoredId }
                    && JsonSerializer.Deserialize<MergeReceipt>(kept, History.JsonOptions) is { } receipt)
                {
                    await Unmerging.UnmergeAsync(db, receipt, restoredId, cancellationToken);
                    db.ChangeTracker.Clear();
                }
            }

            var restored = items.Where(item => results[item.Id].Restored).Select(item => item.Id).ToList();
            await db.TrashItems.Where(row => restored.Contains(row.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return results;
    }

    // A book's rows put back. checkFile says whether a stored file the book names is here; one that isn't is left off.
    public static async Task<TrashRestore> RestoreBookAsync(ShelfDb db, TrashPayload payload, int ownerId, Func<Book, Book>? adjust, CancellationToken cancellationToken,
        Dictionary<(Type, object), bool>? known = null)
    {
        var bookType = db.Model.FindEntityType(typeof(Book))!;
        var bookRows = payload.Tables.FirstOrDefault(table => table.Entity == bookType.Name)?.Rows.FirstOrDefault();
        if (bookRows is null)
        {
            return new TrashRestore(false, null, T("Nothing of the book was kept."));
        }

        known ??= [];
        using var quiet = LoanHistory.Restoring();

        // Inside the caller's transaction when there is one; else in one of its own.
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var book = (Book)Read(bookType, bookRows);
        var oldId = book.Id;
        book.OwnerId = ownerId;
        if (await db.Books.IgnoreQueryFilters().AnyAsync(other => other.Id == oldId, cancellationToken))
        {
            book.Id = 0;
        }

        if (!await FixLinksAsync(db, bookType, book, oldId, 0, known, cancellationToken))
        {
            return new TrashRestore(false, null, T("The reader whose book it was is no longer on this shelf."));
        }

        book = adjust?.Invoke(book) ?? book;
        foreach (var name in payload.Tags.Distinct())
        {
            book.Tags.Add(await db.Tags.FirstOrDefaultAsync(tag => tag.Name == name, cancellationToken) ?? new Tag { Name = name });
        }

        db.Books.Add(book);
        await db.SaveChangesAsync(cancellationToken);
        var newId = book.Id;
        known[(typeof(Book), newId)] = true;

        foreach (var table in payload.Tables.Where(table => table.Entity != bookType.Name))
        {
            if (db.Model.FindEntityType(table.Entity) is not { } type)
            {
                continue;
            }

            var bookKeys = type.GetForeignKeys().Where(key => key.PrincipalEntityType.ClrType == typeof(Book)).ToList();
            var cascade = bookKeys.Any(key => key.DeleteBehavior == DeleteBehavior.Cascade);
            var added = new List<object>();
            foreach (var row in table.Rows)
            {
                if (!cascade)
                {
                    // Left behind when the book went: pointed at the book again, if it is still here and points nowhere.
                    await RelinkAsync(db, type, row, bookKeys, newId, cancellationToken);
                    continue;
                }

                var entity = Read(type, row);
                if (await FixLinksAsync(db, type, entity, oldId, newId, known, cancellationToken))
                {
                    db.Add(entity);
                    added.Add(entity);
                }
            }

            await SaveEachAsync(db, added, cancellationToken);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new TrashRestore(true, newId, null);
    }

    private static async Task<TrashRestore> RestoreRowAsync(ShelfDb db, TrashPayload payload, int? bookId, CancellationToken cancellationToken)
    {
        if (bookId is not int id || !await db.Books.AnyAsync(book => book.Id == id, cancellationToken))
        {
            return new TrashRestore(false, bookId, T("Its book is no longer on your shelf. Bring the book back first."));
        }

        var table = payload.Tables.Single();
        var type = db.Model.FindEntityType(table.Entity)!;
        var entity = Read(type, table.Rows.Single());
        if (!await FixLinksAsync(db, type, entity, id, id, [], cancellationToken))
        {
            return new TrashRestore(false, bookId, T("Its book is no longer on your shelf. Bring the book back first."));
        }

        db.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return new TrashRestore(true, id, null);
    }

    // Every foreign key of a row, looked at: the book it was on becomes the book brought back; a link to anything else
    // that has gone is dropped where it may be, and the row left out where it may not.
    private static async Task<bool> FixLinksAsync(ShelfDb db, IEntityType type, object entity, int oldBookId, int newBookId, Dictionary<(Type, object), bool> known, CancellationToken cancellationToken)
    {
        foreach (var key in type.GetForeignKeys())
        {
            var property = key.Properties.Single();
            var value = property.PropertyInfo!.GetValue(entity);
            if (value is null)
            {
                continue;
            }

            if (key.PrincipalEntityType.ClrType == typeof(Book) && newBookId != 0 && Equals(Convert.ToInt32(value), oldBookId))
            {
                property.PropertyInfo.SetValue(entity, newBookId);
                continue;
            }

            var principal = key.PrincipalEntityType.ClrType;
            if (!known.TryGetValue((principal, value), out var exists))
            {
                exists = await ExistsAsync(db, key.PrincipalEntityType, value, cancellationToken);
                known[(principal, value)] = exists;
            }

            if (exists)
            {
                continue;
            }

            if (property.IsNullable)
            {
                property.PropertyInfo.SetValue(entity, null);
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private static async Task RelinkAsync(ShelfDb db, IEntityType type, Dictionary<string, JsonElement> row, List<IForeignKey> bookKeys, int newBookId, CancellationToken cancellationToken)
    {
        var primary = type.FindPrimaryKey()!.Properties.Single();
        if (!row.TryGetValue(primary.Name, out var keyValue))
        {
            return;
        }

        var key = keyValue.Deserialize(primary.ClrType, History.JsonOptions)!;
        var entity = await db.FindAsync(type.ClrType, [key], cancellationToken);
        if (entity is null)
        {
            return;
        }

        foreach (var foreignKey in bookKeys)
        {
            var property = db.Entry(entity).Property(foreignKey.Properties.Single().Name);
            if (property.CurrentValue is null && row.ContainsKey(foreignKey.Properties.Single().Name))
            {
                property.CurrentValue = newBookId;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    // All at once if it can be; else one at a time, leaving out any that clash with what is here now (a device's place
    // kept since, say).
    private static async Task SaveEachAsync(ShelfDb db, List<object> added, CancellationToken cancellationToken)
    {
        if (added.Count == 0)
        {
            return;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        catch (DbUpdateException)
        {
            foreach (var entity in added)
            {
                db.Entry(entity).State = EntityState.Detached;
            }
        }

        foreach (var entity in added)
        {
            db.Add(entity);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                db.Entry(entity).State = EntityState.Detached;
            }
        }
    }

    // Lets items go for good, and the files only they kept.
    public static async Task<int> ForgetAsync(ShelfDb db, EbookStore ebooks, AudioStore audio, CoverStore covers, IQueryable<TrashItem> items, CancellationToken cancellationToken = default)
    {
        var gone = await items.Select(item => new { item.Id, item.Files }).ToListAsync(cancellationToken);
        if (gone.Count == 0)
        {
            return 0;
        }

        var ids = gone.Select(item => item.Id).ToList();
        foreach (var chunk in ids.Chunk(500))
        {
            await db.TrashItems.IgnoreQueryFilters().Where(item => chunk.Contains(item.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        await DeleteFilesAsync(db, ebooks, audio, covers, gone.SelectMany(item => (item.Files ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)).ToList(), cancellationToken);
        return gone.Count;
    }

    // Only files no book, picture, or other kept item names: a merge may have given one to another book.
    private static async Task DeleteFilesAsync(ShelfDb db, EbookStore ebooks, AudioStore audio, CoverStore covers, List<string> files, CancellationToken cancellationToken)
    {
        foreach (var line in files)
        {
            var (kind, name) = (line[..2], line[2..]);
            var used = kind switch
            {
                "e:" => await db.Books.IgnoreQueryFilters().AnyAsync(book => book.EbookStoredName == name, cancellationToken)
                    || await db.EbookFormats.IgnoreQueryFilters().AnyAsync(format => format.StoredName == name, cancellationToken),
                "a:" => await db.Books.IgnoreQueryFilters().AnyAsync(book => book.AudioStoredName == name, cancellationToken),
                "c:" => await db.Books.IgnoreQueryFilters().AnyAsync(book => book.CoverImage == name, cancellationToken),
                _ => await db.CopyPhotos.IgnoreQueryFilters().AnyAsync(photo => photo.StoredName == name, cancellationToken),
            } || await db.TrashItems.IgnoreQueryFilters().AnyAsync(item => item.Files != null && item.Files.Contains(line), cancellationToken);
            if (used)
            {
                continue;
            }

            switch (kind)
            {
                case "e:":
                    ebooks.Delete(name);
                    break;
                case "a:":
                    audio.Delete(name);
                    break;
                default:
                    covers.Delete(name);
                    break;
            }
        }
    }

    // What has been in the trash longer than it keeps things, every reader's. Items are put in in the order they are
    // deleted, so the oldest are read a page at a time by id until one is young enough (SQLite cannot compare the
    // times themselves).
    public static async Task<int> PurgeAsync(ShelfDb db, EbookStore ebooks, AudioStore audio, CoverStore covers, int days, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        var last = await History.LastBeforeAsync(db.TrashItems.IgnoreQueryFilters().AsNoTracking().Select(item => new History.Stamp { Id = item.Id, At = item.DeletedAt }), cutoff, cancellationToken);
        return last is long id ? await ForgetAsync(db, ebooks, audio, covers, db.TrashItems.IgnoreQueryFilters().Where(item => item.Id <= id), cancellationToken) : 0;
    }

    // Every stored file the trash still keeps, for the file check and the sweep: it is in use until the trash lets go.
    public static async Task<HashSet<string>> KeptFilesAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var lists = await db.TrashItems.IgnoreQueryFilters().Where(item => item.Files != null).Select(item => item.Files!).ToListAsync(cancellationToken);
        return lists.SelectMany(list => list.Split('\n', StringSplitOptions.RemoveEmptyEntries)).ToHashSet(StringComparer.Ordinal);
    }

    private static Dictionary<string, JsonElement> Write(IEntityType type, object entity)
    {
        var row = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in type.GetProperties())
        {
            if (property.PropertyInfo is not { } info)
            {
                continue;
            }

            row[property.Name] = JsonSerializer.SerializeToElement(info.GetValue(entity), property.ClrType, History.JsonOptions);
        }

        return row;
    }

    // A new row from kept values. A key the database makes is left for it to make again, except the book's own.
    private static object Read(IEntityType type, Dictionary<string, JsonElement> row)
    {
        var entity = Activator.CreateInstance(type.ClrType)!;
        var primary = type.FindPrimaryKey()!.Properties;
        foreach (var property in type.GetProperties())
        {
            if (property.PropertyInfo is not { } info || !row.TryGetValue(property.Name, out var value))
            {
                continue;
            }

            if (primary.Count == 1 && primary[0] == property && property.ValueGenerated != ValueGenerated.Never && type.ClrType != typeof(Book))
            {
                continue;
            }

            try
            {
                info.SetValue(entity, value.Deserialize(property.ClrType, History.JsonOptions));
            }
            catch (JsonException)
            {
                // A value an older Shelf kept that this one reads differently: left at its default.
            }
        }

        return entity;
    }

    private static readonly MethodInfo RowsMethod = typeof(Trash).GetMethod(nameof(RowsOfAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo ExistsMethod = typeof(Trash).GetMethod(nameof(ExistsOfAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static Task<List<object>> RowsAsync(ShelfDb db, Type type, string property, Type propertyType, List<int> bookIds, CancellationToken cancellationToken) =>
        (Task<List<object>>)RowsMethod.MakeGenericMethod(type).Invoke(null, [db, property, propertyType, bookIds, cancellationToken])!;

    // The rows whose property is one of the books, as ids.Contains(row.Property) would ask for them.
    private static async Task<List<object>> RowsOfAsync<T>(ShelfDb db, string property, Type propertyType, List<int> bookIds, CancellationToken cancellationToken)
        where T : class
    {
        var row = Expression.Parameter(typeof(T), "row");
        var value = Expression.Call(typeof(EF), nameof(EF.Property), [propertyType], row, Expression.Constant(property));
        var list = typeof(List<>).MakeGenericType(propertyType);
        var ids = (System.Collections.IList)Activator.CreateInstance(list)!;
        bookIds.ForEach(id => ids.Add(id));
        var match = Expression.Lambda<Func<T, bool>>(Expression.Call(Expression.Constant(ids, list), list.GetMethod(nameof(List<int>.Contains))!, value), row);
        return [.. await db.Set<T>().IgnoreQueryFilters().AsNoTracking().Where(match).ToListAsync(cancellationToken)];
    }

    private static Task<bool> ExistsAsync(ShelfDb db, IEntityType type, object key, CancellationToken cancellationToken)
    {
        var primary = type.FindPrimaryKey()!.Properties.Single();
        return (Task<bool>)ExistsMethod.MakeGenericMethod(type.ClrType).Invoke(null, [db, primary.Name, primary.ClrType, key, cancellationToken])!;
    }

    private static Task<bool> ExistsOfAsync<T>(ShelfDb db, string property, Type propertyType, object key, CancellationToken cancellationToken)
        where T : class
    {
        var row = Expression.Parameter(typeof(T), "row");
        var value = Expression.Call(typeof(EF), nameof(EF.Property), [propertyType], row, Expression.Constant(property));
        var match = Expression.Lambda<Func<T, bool>>(Expression.Equal(value, Expression.Constant(Convert.ChangeType(key, propertyType), propertyType)), row);
        return db.Set<T>().IgnoreQueryFilters().AnyAsync(match, cancellationToken);
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}

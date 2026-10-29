using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

// What one merge moved from the book merged away onto the book kept, kept with the merged-away book in the trash, so
// that restoring it from there takes back what was its own: each row by its id, the kept book's details as they were
// before, the tags and files it gained, the links between the two, and the reading days added together.
public sealed class MergeReceipt
{
    public int KeptBookId { get; set; }

    // By table: the ids of the rows the merge pointed at the kept book.
    public Dictionary<string, List<int>> Moved { get; set; } = [];

    public List<int> LinksFrom { get; set; } = [];
    public List<int> LinksTo { get; set; } = [];

    // Links between the two books, removed as they would have joined the kept book to itself.
    public List<MergeLink> LinksRemoved { get; set; } = [];

    // Collections the merged-away book was in and the kept book was not, its place there moved to the kept book.
    public List<int> Collections { get; set; } = [];

    // A day of reading or listening on both books, its time added to the kept book's day.
    public List<MergeDay> ListeningAdded { get; set; } = [];
    public List<MergeDay> ReadingAdded { get; set; } = [];

    // The kept book's details the merge changed: each as it was before, and as the merge left it.
    public Dictionary<string, MergeChange> Kept { get; set; } = [];

    public List<string> TagsAdded { get; set; } = [];

    // Stored files the kept book took because it had none of the kind.
    public string? Ebook { get; set; }

    // The merged-away book's main e-book, kept by the other as another format.
    public string? EbookAsFormat { get; set; }
    public string? Audio { get; set; }
    public string? Cover { get; set; }
}

public sealed record MergeLink(bool FromMerged, LinkKind Kind, string? Note);

public sealed record MergeDay(int IntoId, int ReaderId, DateOnly Day, int Seconds, int DeviceSeconds = 0, int DeviceBefore = 0);

public sealed record MergeChange(JsonElement Before, JsonElement After);

public static class Unmerging
{
    public static void Moved(MergeReceipt receipt, string table, IEnumerable<int> ids)
    {
        var list = receipt.Moved.TryGetValue(table, out var known) ? known : receipt.Moved[table] = [];
        list.AddRange(ids);
    }

    // The kept book's changed details, read off its tracked entry before the merge is saved.
    public static void NoteKept(ShelfDb db, Book keep, MergeReceipt receipt)
    {
        db.ChangeTracker.DetectChanges();
        foreach (var property in db.Entry(keep).Properties.Where(property => property.IsModified))
        {
            receipt.Kept[property.Metadata.Name] = new MergeChange(
                JsonSerializer.SerializeToElement(property.OriginalValue, property.Metadata.ClrType, History.JsonOptions),
                JsonSerializer.SerializeToElement(property.CurrentValue, property.Metadata.ClrType, History.JsonOptions));
        }
    }

    // Everything the merge moved goes back to the book restored from the trash, as long as it is still on the kept
    // book as the merge left it: a row moved since, a detail changed since, or a day changed since stays as it is.
    // Inside the restore's transaction.
    public static async Task UnmergeAsync(ShelfDb db, MergeReceipt receipt, int restoredId, CancellationToken cancellationToken = default)
    {
        var keptId = receipt.KeptBookId;
        var kept = await db.Books.IgnoreQueryFilters().Include(book => book.Tags).FirstOrDefaultAsync(book => book.Id == keptId, cancellationToken);
        var restored = await db.Books.IgnoreQueryFilters().FirstOrDefaultAsync(book => book.Id == restoredId, cancellationToken);
        if (kept is null || restored is null || kept.OwnerId != restored.OwnerId)
        {
            return;
        }

        List<int> Ids(string table) => receipt.Moved.GetValueOrDefault(table) ?? [];
        var quotes = Ids(nameof(ShelfDb.Quotes));
        await db.Quotes.IgnoreQueryFilters().Where(row => quotes.Contains(row.Id) && row.BookId == keptId).ExecuteUpdateAsync(set => set.SetProperty(row => row.BookId, restoredId), cancellationToken);
        var highlights = Ids(nameof(ShelfDb.Highlights));
        await db.Highlights.IgnoreQueryFilters().Where(row => highlights.Contains(row.Id) && row.BookId == keptId).ExecuteUpdateAsync(set => set.SetProperty(row => row.BookId, restoredId), cancellationToken);
        var sessions = Ids(nameof(ShelfDb.Sessions));
        await db.Sessions.IgnoreQueryFilters().Where(row => sessions.Contains(row.Id) && row.BookId == keptId).ExecuteUpdateAsync(set => set.SetProperty(row => row.BookId, restoredId), cancellationToken);
        var bookmarks = Ids(nameof(ShelfDb.AudioBookmarks));
        await db.AudioBookmarks.IgnoreQueryFilters().Where(row => bookmarks.Contains(row.Id) && row.BookId == keptId).ExecuteUpdateAsync(set => set.SetProperty(row => row.BookId, restoredId), cancellationToken);
        var places = Ids(nameof(ShelfDb.KosyncPlaces));
        await db.KosyncPlaces.IgnoreQueryFilters().Where(row => places.Contains(row.Id) && row.BookId == keptId).ExecuteUpdateAsync(set => set.SetProperty(row => row.BookId, restoredId), cancellationToken);
        var reads = Ids(nameof(ShelfDb.PastReads));
        await db.PastReads.IgnoreQueryFilters().Where(row => reads.Contains(row.Id) && row.BookId == keptId).ExecuteUpdateAsync(set => set.SetProperty(row => row.BookId, restoredId), cancellationToken);
        var photos = Ids(nameof(ShelfDb.CopyPhotos));
        await db.CopyPhotos.IgnoreQueryFilters().Where(row => photos.Contains(row.Id) && row.BookId == keptId).ExecuteUpdateAsync(set => set.SetProperty(row => row.BookId, restoredId), cancellationToken);
        var journal = Ids(nameof(ShelfDb.JournalEntries));
        await db.JournalEntries.IgnoreQueryFilters().Where(row => journal.Contains(row.Id) && row.BookId == keptId).ExecuteUpdateAsync(set => set.SetProperty(row => row.BookId, restoredId), cancellationToken);
        var listened = Ids(nameof(ShelfDb.ListeningDays));
        await db.ListeningDays.IgnoreQueryFilters().Where(row => listened.Contains(row.Id) && row.BookId == keptId).ExecuteUpdateAsync(set => set.SetProperty(row => row.BookId, restoredId), cancellationToken);
        var formats = Ids(nameof(ShelfDb.EbookFormats));
        await db.EbookFormats.IgnoreQueryFilters().Where(row => formats.Contains(row.Id) && row.BookId == keptId).ExecuteUpdateAsync(set => set.SetProperty(row => row.BookId, restoredId), cancellationToken);
        var listenedSessions = Ids(nameof(ShelfDb.ListeningSessions));
        await db.ListeningSessions.IgnoreQueryFilters().Where(row => listenedSessions.Contains(row.Id) && row.BookId == keptId).ExecuteUpdateAsync(set => set.SetProperty(row => row.BookId, restoredId), cancellationToken);
        var playlistItems = Ids(nameof(ShelfDb.PlaylistItems));
        await db.PlaylistItems.IgnoreQueryFilters().Where(row => playlistItems.Contains(row.Id) && row.BookId == keptId).ExecuteUpdateAsync(set => set.SetProperty(row => row.BookId, restoredId), cancellationToken);
        var values = Ids(nameof(ShelfDb.CustomValues));
        await db.CustomValues.IgnoreQueryFilters().Where(row => values.Contains(row.Id) && row.BookId == keptId).ExecuteUpdateAsync(set => set.SetProperty(row => row.BookId, restoredId), cancellationToken);
        var readDays = Ids(nameof(ShelfDb.ReadingDays));
        await db.ReadingDays.IgnoreQueryFilters().Where(row => readDays.Contains(row.Id) && row.BookId == keptId).ExecuteUpdateAsync(set => set.SetProperty(row => row.BookId, restoredId), cancellationToken);
        await db.BookLinks.Where(link => receipt.LinksFrom.Contains(link.Id) && link.FromBookId == keptId).ExecuteUpdateAsync(set => set.SetProperty(link => link.FromBookId, restoredId), cancellationToken);
        await db.BookLinks.Where(link => receipt.LinksTo.Contains(link.Id) && link.ToBookId == keptId).ExecuteUpdateAsync(set => set.SetProperty(link => link.ToBookId, restoredId), cancellationToken);
        foreach (var link in receipt.LinksRemoved)
        {
            db.BookLinks.Add(new BookLink { FromBookId = link.FromMerged ? restoredId : keptId, ToBookId = link.FromMerged ? keptId : restoredId, Kind = link.Kind, Note = link.Note, CreatedAt = DateTimeOffset.UtcNow });
        }

        // A collection's place goes back only where the restored book has none there of its own.
        var theirs = await db.CollectionBooks.IgnoreQueryFilters().Where(entry => entry.BookId == restoredId).Select(entry => entry.CollectionId).ToListAsync(cancellationToken);
        var collections = receipt.Collections.Except(theirs).ToList();
        await db.CollectionBooks.IgnoreQueryFilters().Where(entry => collections.Contains(entry.CollectionId) && entry.BookId == keptId)
            .ExecuteUpdateAsync(set => set.SetProperty(entry => entry.BookId, restoredId), cancellationToken);

        // Time added to a day of the kept book is taken off it, and the restored book has its own day again.
        foreach (var day in receipt.ListeningAdded)
        {
            if (await db.ListeningDays.IgnoreQueryFilters().FirstOrDefaultAsync(row => row.Id == day.IntoId && row.BookId == keptId, cancellationToken) is { } into && into.Seconds >= day.Seconds)
            {
                into.Seconds -= day.Seconds;
                db.ListeningDays.Add(new ListeningDay { BookId = restoredId, ReaderId = day.ReaderId, Day = day.Day, Seconds = day.Seconds });
            }
        }

        foreach (var day in receipt.ReadingAdded)
        {
            if (await db.ReadingDays.IgnoreQueryFilters().FirstOrDefaultAsync(row => row.Id == day.IntoId && row.BookId == keptId, cancellationToken) is { } into && into.Seconds >= day.Seconds)
            {
                into.Seconds -= day.Seconds;
                into.DeviceSeconds = into.DeviceSeconds == Math.Max(day.DeviceBefore, day.DeviceSeconds) ? day.DeviceBefore : into.DeviceSeconds;
                db.ReadingDays.Add(new ReadingDay { BookId = restoredId, ReaderId = day.ReaderId, Day = day.Day, Seconds = day.Seconds, DeviceSeconds = day.DeviceSeconds });
            }
        }

        // The kept book's details as they were, each one the merge changed and nothing has changed since.
        var entry = db.Entry(kept);
        foreach (var (name, change) in receipt.Kept)
        {
            if (entry.Metadata.FindProperty(name) is not { } property)
            {
                continue;
            }

            var current = JsonSerializer.SerializeToElement(entry.Property(name).CurrentValue, property.ClrType, History.JsonOptions);
            if (JsonElement.DeepEquals(current, change.After))
            {
                entry.Property(name).CurrentValue = change.Before.Deserialize(property.ClrType, History.JsonOptions);
            }
        }

        // A file the kept book took goes back with it, once the kept book has let it go.
        if (receipt.Ebook is { } ebook && kept.EbookStoredName is null)
        {
            restored.EbookStoredName = ebook;
        }

        if (receipt.EbookAsFormat is { } asFormat && restored.EbookStoredName is null)
        {
            await db.EbookFormats.IgnoreQueryFilters().Where(row => row.BookId == keptId && row.StoredName == asFormat).ExecuteDeleteAsync(cancellationToken);
            restored.EbookStoredName = asFormat;
        }

        if (receipt.Audio is { } recording && kept.AudioStoredName is null)
        {
            restored.AudioStoredName = recording;
        }

        if (receipt.Cover is { } cover && kept.CoverImage is null)
        {
            restored.CoverImage = cover;
        }

        foreach (var name in receipt.TagsAdded)
        {
            if (kept.Tags.FirstOrDefault(tag => tag.Name == name) is { } tag)
            {
                kept.Tags.Remove(tag);
            }
        }

        if (receipt.Ebook is not null && restored.EbookStoredName is not null)
        {
            var scans = Ids(nameof(ShelfDb.OcrScans));
            await db.OcrScans.IgnoreQueryFilters().Where(row => scans.Contains(row.Id) && row.BookId == keptId).ExecuteUpdateAsync(set => set.SetProperty(row => row.BookId, restoredId), cancellationToken);
            var texts = Ids(nameof(ShelfDb.BookTexts));
            await db.BookTexts.IgnoreQueryFilters().Where(row => texts.Contains(row.Id) && row.BookId == keptId).ExecuteUpdateAsync(set => set.SetProperty(row => row.BookId, restoredId), cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

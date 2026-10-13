using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

// An event as a reader's feed keeps it: the same JSON a signed webhook is sent, for a script that would rather ask
// than be told. The latest MaxKept are kept for each reader; older ones go as new ones come.
public sealed class EventRecord
{
    public const int MaxKept = 1000;

    public long Id { get; set; }
    public int ReaderId { get; set; }
    public required string Kind { get; set; }
    public required string Payload { get; set; }
}

// One event, with the id to ask after next time.
public sealed record FeedEvent(long Id, JsonElement Event);

// A page of the feed: the events after the id asked for, oldest first, and the id to ask after next. More is true
// when there are more waiting than one page holds.
public sealed record EventPage(FeedEvent[] Events, long Next, bool More);

public static class EventFeed
{
    public const int MaxTake = 200;

    public static void Map(RouteGroupBuilder books) =>
        books.MapGet("/events", async (long? after, int? take, string? kind, ShelfDb db, CancellationToken cancellationToken) =>
            TypedResults.Ok(await PageAsync(db, after ?? 0, Math.Clamp(take ?? 100, 1, MaxTake), kind, cancellationToken)));

    public static async Task<EventPage> PageAsync(ShelfDb db, long after, int take, string? kind = null, CancellationToken cancellationToken = default)
    {
        var me = db.ReaderId;
        var query = db.EventRecords.AsNoTracking().Where(item => item.ReaderId == me && item.Id > after);
        if (!string.IsNullOrWhiteSpace(kind))
        {
            var kinds = kind.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            query = query.Where(item => kinds.Contains(item.Kind));
        }

        var rows = await query.OrderBy(item => item.Id).Take(take + 1).ToListAsync(cancellationToken);
        var shown = rows.Take(take).Select(row => new FeedEvent(row.Id, JsonDocument.Parse(row.Payload).RootElement.Clone())).ToArray();
        return new EventPage(shown, shown.Length == 0 ? after : shown[^1].Id, rows.Count > take);
    }

    // Keeps an event for each reader it concerns, and lets the oldest go past the limit.
    public static async Task RecordAsync(ShelfDb db, IReadOnlyDictionary<int, string> payloads, string kind, CancellationToken cancellationToken)
    {
        foreach (var (reader, payload) in payloads)
        {
            db.EventRecords.Add(new EventRecord { ReaderId = reader, Kind = kind, Payload = payload });
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var reader in payloads.Keys)
        {
            var keepFrom = await db.EventRecords.Where(item => item.ReaderId == reader).OrderByDescending(item => item.Id)
                .Skip(EventRecord.MaxKept).Select(item => (long?)item.Id).FirstOrDefaultAsync(cancellationToken);
            if (keepFrom is long oldest)
            {
                await db.EventRecords.Where(item => item.ReaderId == reader && item.Id <= oldest).ExecuteDeleteAsync(cancellationToken);
            }
        }
    }
}

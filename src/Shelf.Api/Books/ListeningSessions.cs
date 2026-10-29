using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// One sitting with a recording, as Audiobookshelf keeps its listening sessions: which book, on what, when it began
// and last played, how long was heard, and from where to where in the book. A device that goes on telling within a
// quarter of an hour goes on the same sitting. Each reader's are their own; no one else sees them, admins included.
public sealed class ListeningSession
{
    public const int MaxDeviceLength = 80;

    public int Id { get; set; }
    public int ReaderId { get; set; }
    public int BookId { get; set; }
    public Book? Book { get; set; }
    public required string Device { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastAt { get; set; }
    public int Seconds { get; set; }

    // Seconds into the whole recording, where known.
    public double? FromSeconds { get; set; }
    public double? ToSeconds { get; set; }
}

public sealed record ListeningSessionView(int Id, int BookId, string Title, string Device, DateTimeOffset StartedAt, DateTimeOffset LastAt, int Seconds, double? FromSeconds, double? ToSeconds);

public static class ListeningSessions
{
    public static readonly TimeSpan SameSitting = TimeSpan.FromMinutes(15);

    public static void Map(RouteGroupBuilder books) =>
        books.MapGet("/listening/sessions", async (ShelfDb db, CancellationToken cancellationToken, int? book = null, int take = 100) =>
            TypedResults.Ok(await ListAsync(db, book, Math.Clamp(take, 1, 500), cancellationToken))).WithTags("Reading");

    // Listening heard, put on the device's sitting with the book, or a new one.
    public static async Task NoteAsync(ShelfDb db, int bookId, string device, int seconds, double? at, CancellationToken cancellationToken = default)
    {
        if (seconds < 1)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var name = device.Length <= ListeningSession.MaxDeviceLength ? device : device[..ListeningSession.MaxDeviceLength];
        var readerId = db.ReaderId;
        // Moments can't be ordered in SQLite, so the last of the few for this book and device is found here.
        var last = (await db.ListeningSessions.Where(item => item.ReaderId == readerId && item.BookId == bookId && item.Device == name).OrderByDescending(item => item.Id).Take(3).ToListAsync(cancellationToken))
            .MaxBy(item => item.LastAt);
        if (last is not null && now - last.LastAt <= SameSitting)
        {
            last.Seconds += seconds;
            last.LastAt = now;
            last.ToSeconds = at ?? last.ToSeconds;
        }
        else
        {
            db.ListeningSessions.Add(new ListeningSession
            {
                ReaderId = readerId,
                BookId = bookId,
                Device = name,
                StartedAt = now.AddSeconds(-seconds),
                LastAt = now,
                Seconds = seconds,
                FromSeconds = at is double end ? Math.Max(0, end - seconds) : null,
                ToSeconds = at,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task<ListeningSessionView[]> ListAsync(ShelfDb db, int? bookId, int take, CancellationToken cancellationToken = default)
    {
        var readerId = db.ReaderId;
        var rows = await db.ListeningSessions.AsNoTracking().Where(item => item.ReaderId == readerId && (bookId == null || item.BookId == bookId))
            .OrderByDescending(item => item.Id).Take(take)
            .Select(item => new { Session = item, Title = item.Book == null ? null : item.Book.Title })
            .ToListAsync(cancellationToken);
        return [.. rows.OrderByDescending(row => row.Session.StartedAt).Select(row => new ListeningSessionView(row.Session.Id, row.Session.BookId,
            row.Title ?? T("A book no longer on your shelf"), row.Session.Device, row.Session.StartedAt, row.Session.LastAt, row.Session.Seconds, row.Session.FromSeconds, row.Session.ToSeconds))];
    }

    // The browser in words from its user agent: "Firefox on Android". Only the names, nothing that would tell one
    // device from another.
    public static string Describe(string? userAgent)
    {
        var agent = userAgent ?? "";
        var browser = agent.Contains("Firefox/", StringComparison.Ordinal) ? "Firefox"
            : agent.Contains("Edg/", StringComparison.Ordinal) ? "Edge"
            : agent.Contains("Chrome/", StringComparison.Ordinal) ? "Chrome"
            : agent.Contains("Safari/", StringComparison.Ordinal) ? "Safari"
            : null;
        var system = agent.Contains("Android", StringComparison.Ordinal) ? "Android"
            : agent.Contains("iPhone", StringComparison.Ordinal) ? "iPhone"
            : agent.Contains("iPad", StringComparison.Ordinal) ? "iPad"
            : agent.Contains("Mac OS", StringComparison.Ordinal) ? "Mac"
            : agent.Contains("Windows", StringComparison.Ordinal) ? "Windows"
            : agent.Contains("Linux", StringComparison.Ordinal) ? "Linux"
            : null;
        return (browser, system) switch
        {
            (null, null) => T("Shelf's player"),
            (null, _) => T("Shelf's player on {0}", system),
            (_, null) => T("Shelf's player in {0}", browser),
            _ => T("Shelf's player in {0} on {1}", browser, system),
        };
    }
}

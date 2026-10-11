using System.Threading.Channels;

namespace Shelf.Api.Books;

// Something that happened on the shelf, told after it is saved: to notifications for the readers it concerns, and to
// an admin's webhooks. It carries ids, not text, so each listener says it in its own words and its reader's language.
public sealed record ShelfEvent(
    string Kind,
    int ActorId,
    int? BookId = null,
    int? ReaderId = null,
    int? ClubId = null,
    int? PostId = null,
    int Count = 1)
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

public static class EventKinds
{
    public const string BookAdded = "book.added";
    public const string BookStarted = "book.started";
    public const string BookFinished = "book.finished";
    public const string BookLent = "book.lent";
    public const string BookReturned = "book.returned";
    public const string LoanAsked = "loan.asked";
    public const string ClubPosted = "club.posted";
    public const string ClubJoined = "club.joined";
    public const string SeriesFound = "series.found";

    public static readonly IReadOnlyList<string> All = [BookAdded, BookStarted, BookFinished, BookLent, BookReturned, LoanAsked, ClubPosted, ClubJoined, SeriesFound];
}

public interface IShelfEventHandler
{
    Task HandleAsync(ShelfEvent shelfEvent, CancellationToken cancellationToken);
}

// Events wait in a queue and go to each listener in the background, so saving never waits on a phone or a webhook.
public sealed class ShelfEvents(IServiceProvider services, ILogger<ShelfEvents> logger) : BackgroundService
{
    private readonly Channel<ShelfEvent> _queue = Channel.CreateUnbounded<ShelfEvent>(new UnboundedChannelOptions { SingleReader = true });
    private int _waiting;

    public void Publish(ShelfEvent shelfEvent)
    {
        Interlocked.Increment(ref _waiting);
        if (!_queue.Writer.TryWrite(shelfEvent))
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    // Whether every event published so far has been handled; for tests, and for a quiet shutdown.
    public bool Idle => Volatile.Read(ref _waiting) == 0;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var handlers = services.GetServices<IShelfEventHandler>().ToList();
        await foreach (var shelfEvent in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            foreach (var handler in handlers)
            {
                try
                {
                    await handler.HandleAsync(shelfEvent, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not tell {Handler} about {Kind}.", handler.GetType().Name, shelfEvent.Kind);
                }
            }

            Interlocked.Decrement(ref _waiting);
        }
    }
}

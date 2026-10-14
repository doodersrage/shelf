using System.Collections.Concurrent;

namespace Shelf.Api.Books;

public sealed record Heartbeat(string Job, DateTimeOffset At, string? Problem);

// When each of the server's own jobs last ran, and whether it went wrong, for the admin's health page. Kept in
// memory: a restart starts the record again.
public static class Heartbeats
{
    private static readonly ConcurrentDictionary<string, Heartbeat> Beats = new();

    public static DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;

    public static void Beat(string job, string? problem = null) =>
        Beats[job] = new Heartbeat(job, DateTimeOffset.UtcNow, problem is { Length: > 300 } ? problem[..300] : problem);

    public static IReadOnlyCollection<Heartbeat> All => [.. Beats.Values.OrderBy(beat => beat.Job, StringComparer.Ordinal)];
}

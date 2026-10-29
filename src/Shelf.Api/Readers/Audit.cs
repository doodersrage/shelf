using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Readers;

// Who did what to which account: sign-ups, failed sign-ins, passwords, admin rights, two-step sign-in, devices,
// keys, restores, and backups. Names are kept as text, so the record outlives a removed reader.
public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public int? ActorId { get; set; }
    public string? ActorName { get; set; }
    public required string Action { get; set; }
    public int? TargetId { get; set; }
    public string? TargetName { get; set; }
    public string? Detail { get; set; }
}

public sealed record AuditLine(DateTimeOffset At, string? Actor, string Action, string? Target, string? Detail);

public static class Audit
{
    public const int Keep = 5000;

    // Failed sign-ins are kept to their own number, so a flood of them (from many addresses, say) can't push out the
    // rest of the record; and the same failure for one account within the hour is one line, counted.
    public const int KeepFailures = 1000;
    private const string FailurePrefix = "A sign-in failed";
    private static readonly TimeSpan FoldWithin = TimeSpan.FromHours(1);

    // The signed-in reader is the one acting; with nobody signed in (a sign-up, a reset by email) it is the reader acted on.
    public static async Task NoteAsync(ShelfDb db, string action, Reader? target = null, string? detail = null, CancellationToken cancellationToken = default)
    {
        var actor = db.ReaderId != 0 ? await db.Readers.AsNoTracking().FirstOrDefaultAsync(reader => reader.Id == db.ReaderId, cancellationToken) : null;
        actor ??= target;
        if (action.StartsWith(FailurePrefix, StringComparison.Ordinal) && actor is not null)
        {
            var since = DateTimeOffset.UtcNow - FoldWithin;
            var last = await db.AuditEntries.Where(entry => entry.Action == action && entry.ActorId == actor.Id && entry.TargetId == null)
                .OrderByDescending(entry => entry.Id).FirstOrDefaultAsync(cancellationToken);
            if (last is not null && last.At >= since)
            {
                var count = last.Detail is { Length: > 1 } times && times[0] == '×' && int.TryParse(times[1..], out var was) ? was + 1 : 2;
                (last.At, last.Detail) = (DateTimeOffset.UtcNow, $"×{count}");
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
        }

        db.AuditEntries.Add(new AuditEntry
        {
            At = DateTimeOffset.UtcNow,
            ActorId = actor?.Id,
            ActorName = actor?.Name,
            Action = Fit(action, 200)!,
            TargetId = target?.Id == actor?.Id ? null : target?.Id,
            TargetName = target?.Id == actor?.Id ? null : target?.Name,
            Detail = Fit(detail, 300),
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task<AuditLine[]> RecentAsync(ShelfDb db, int? readerId = null, int take = 200, CancellationToken cancellationToken = default)
    {
        var entries = db.AuditEntries.AsNoTracking();
        if (readerId is { } id)
        {
            entries = entries.Where(entry => entry.ActorId == id || entry.TargetId == id);
        }

        var rows = await entries.OrderByDescending(entry => entry.Id).Take(take).ToListAsync(cancellationToken);
        return rows.Select(entry => new AuditLine(entry.At, entry.ActorName, entry.Action, entry.TargetName, entry.Detail)).ToArray();
    }

    // Only the newest entries are kept: failed sign-ins to their own number first, then everything.
    public static async Task<int> TrimAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var removed = 0;
        var failures = db.AuditEntries.Where(entry => entry.Action.StartsWith(FailurePrefix));
        if (await failures.OrderByDescending(entry => entry.Id).Skip(KeepFailures).Select(entry => (long?)entry.Id).FirstOrDefaultAsync(cancellationToken) is { } oldFailure)
        {
            removed += await failures.Where(entry => entry.Id <= oldFailure).ExecuteDeleteAsync(cancellationToken);
        }

        var cutoff = await db.AuditEntries.OrderByDescending(entry => entry.Id).Skip(Keep).Select(entry => (long?)entry.Id).FirstOrDefaultAsync(cancellationToken);
        return removed + (cutoff is { } id ? await db.AuditEntries.Where(entry => entry.Id <= id).ExecuteDeleteAsync(cancellationToken) : 0);
    }

    private static string? Fit(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length > length ? value[..length] : value;
}

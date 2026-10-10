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

    // The signed-in reader is the one acting; with nobody signed in (a sign-up, a reset by email) it is the reader acted on.
    public static async Task NoteAsync(ShelfDb db, string action, Reader? target = null, string? detail = null, CancellationToken cancellationToken = default)
    {
        var actor = db.ReaderId != 0 ? await db.Readers.AsNoTracking().FirstOrDefaultAsync(reader => reader.Id == db.ReaderId, cancellationToken) : null;
        actor ??= target;
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

    // Only the newest entries are kept.
    public static async Task<int> TrimAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var cutoff = await db.AuditEntries.OrderByDescending(entry => entry.Id).Skip(Keep).Select(entry => (long?)entry.Id).FirstOrDefaultAsync(cancellationToken);
        return cutoff is { } id ? await db.AuditEntries.Where(entry => entry.Id <= id).ExecuteDeleteAsync(cancellationToken) : 0;
    }

    private static string? Fit(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length > length ? value[..length] : value;
}

using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public enum ConnectionHealth
{
    Off,
    Waiting,
    Working,
    Trouble,
}

// One thing the shelf trades with, how it stands, and where to set it up or put it right.
public sealed record ConnectionStatus(string Name, ConnectionHealth Health, string Note, string Href);

// Every connection a reader can make, in one list, so they can see at a glance whether their books are flowing: set
// up or not, when each was last heard from, and what went wrong, if anything did. Quiet is not trouble: a connection
// set up and simply with nothing new says so plainly.
public static class Connections
{
    public static async Task<List<ConnectionStatus>> ForReaderAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var me = db.ReaderId;
        var reader = await db.Readers.AsNoTracking().Where(item => item.Id == me)
            .Select(item => new
            {
                item.KosyncHash, item.KoboTokenHash, item.ReadwiseToken, item.ReadwiseAuto, item.ReadwiseAutoImport, item.ReadwiseSentAt,
                item.ReadwiseImportedAt, item.HardcoverToken, item.HardcoverAuto,
                item.ReadwiseProblem, item.ReadwiseProblemAt, item.HardcoverProblem, item.HardcoverProblemAt,
            })
            .FirstOrDefaultAsync(cancellationToken);
        var list = new List<ConnectionStatus>();
        if (reader is null)
        {
            return list;
        }

        // SQLite can't order by a time with its offset, so the latest is found here; a row a book is few enough.
        var koreader = (await db.KosyncPlaces.AsNoTracking().Where(place => place.ReaderId == me)
            .Select(place => new { place.UpdatedAt, place.Device }).ToListAsync(cancellationToken)).MaxBy(place => place.UpdatedAt);
        list.Add(reader.KosyncHash is null
            ? new("KOReader", ConnectionHealth.Off, T("Not set up."), "/sync#koreader")
            : koreader is null
                ? new("KOReader", ConnectionHealth.Waiting, T("Set up. Nothing has come from it yet: open a book from this shelf in KOReader and sync."), "/sync#koreader")
                : new("KOReader", ConnectionHealth.Working, string.IsNullOrWhiteSpace(koreader.Device)
                    ? T("Last synced {0}.", koreader.UpdatedAt.Medium())
                    : T("Last synced {0}, from {1}.", koreader.UpdatedAt.Medium(), koreader.Device), "/sync#koreader"));

        var kobo = (await db.KoboEntries.AsNoTracking().Where(entry => entry.ReaderId == me).Select(entry => entry.SyncedAt).ToListAsync(cancellationToken))
            .Select(synced => (DateTimeOffset?)synced).Max();
        list.Add(reader.KoboTokenHash is null
            ? new("Kobo", ConnectionHealth.Off, T("Not set up."), "/sync#kobo")
            : kobo is DateTimeOffset synced
                ? new("Kobo", ConnectionHealth.Working, T("Last synced {0}.", synced.Medium()), "/sync#kobo")
                : new("Kobo", ConnectionHealth.Waiting, T("Set up. Nothing has come from it yet: sync the Kobo over Wi-Fi."), "/sync#kobo"));

        var abs = await db.AbsSyncs.AsNoTracking().Where(sync => sync.ReaderId == me)
            .Select(sync => new { sync.LibraryName, sync.CheckedAt, sync.LastFound, sync.Problem }).FirstOrDefaultAsync(cancellationToken);
        list.Add(abs switch
        {
            null => new("Audiobookshelf", ConnectionHealth.Off, T("Not kept in step."), "/import/audiobookshelf"),
            { Problem: { Length: > 0 } problem } => new("Audiobookshelf", ConnectionHealth.Trouble, T("The last look at {0} went wrong: {1}", abs.LibraryName, problem), "/import/audiobookshelf"),
            { CheckedAt: DateTimeOffset checkedAt } => new("Audiobookshelf", ConnectionHealth.Working, abs.LastFound is > 0
                ? T("{0} last looked at {1}, and {2} new came across.", abs.LibraryName, checkedAt.Medium(), Localization.Counts.Books(abs.LastFound.Value))
                : T("{0} last looked at {1}; nothing new.", abs.LibraryName, checkedAt.Medium()), "/import/audiobookshelf"),
            _ => new("Audiobookshelf", ConnectionHealth.Waiting, T("{0} is kept in step; the first look is still to come.", abs.LibraryName), "/import/audiobookshelf"),
        });

        var readwiseLast = new[] { reader.ReadwiseSentAt, reader.ReadwiseImportedAt }.Max();
        list.Add(reader.ReadwiseToken is null
            ? new("Readwise", ConnectionHealth.Off, T("Not connected."), "/account#readwise")
            : reader.ReadwiseProblem is SyncProblem readwiseProblem
                ? new("Readwise", ConnectionHealth.Trouble, Trouble(readwiseProblem, reader.ReadwiseProblemAt, "Readwise"), "/account#readwise")
            : readwiseLast is DateTimeOffset traded
                ? new("Readwise", ConnectionHealth.Working, T("Connected. Highlights last traded {0}.", traded.Medium()), "/account#readwise")
                : new("Readwise", ConnectionHealth.Waiting, reader.ReadwiseAuto || reader.ReadwiseAutoImport
                    ? T("Connected. Highlights go within the hour.")
                    : T("Connected. Nothing sent yet: send from Account, or turn on sending by itself."), "/account#readwise"));

        list.Add(reader.HardcoverToken is null
            ? new("Hardcover", ConnectionHealth.Off, T("Not connected."), "/account#hardcover")
            : reader.HardcoverProblem is SyncProblem hardcoverProblem
                ? new("Hardcover", ConnectionHealth.Trouble, Trouble(hardcoverProblem, reader.HardcoverProblemAt, "Hardcover"), "/account#hardcover")
            : new("Hardcover", ConnectionHealth.Working, reader.HardcoverAuto
                ? T("Connected. Changes go every hour.")
                : T("Connected. Changes go when you send them, from Account."), "/account#hardcover"));

        var sync = await db.Settings.AsNoTracking().Where(setting => setting.Id == me).Select(setting => setting.SyncAddress).FirstOrDefaultAsync(cancellationToken);
        list.Add(string.IsNullOrWhiteSpace(sync)
            ? new(T("Another shelf"), ConnectionHealth.Off, T("Not set up."), "/sync#another-shelf")
            : new(T("Another shelf"), ConnectionHealth.Working, T("Trading with {0}.", sync), "/sync#another-shelf"));
        return list;
    }

    // What went wrong the last time, and when, said so the reader knows what to do.
    public static string Trouble(SyncProblem problem, DateTimeOffset? at, string service) => at is DateTimeOffset when
        ? T("The last try, {0}, went wrong. {1}", when.Medium(), SyncProblems.Describe(problem, service))
        : SyncProblems.Describe(problem, service);

    public static string Label(ConnectionHealth health) => health switch
    {
        ConnectionHealth.Working => T("Working"),
        ConnectionHealth.Waiting => T("Waiting"),
        ConnectionHealth.Trouble => T("Needs a look"),
        _ => T("Off"),
    };
}

using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api;

// The day and time where a reader is: by the time zone they keep (an IANA name such as America/Chicago), else the
// server's own, which is UTC in the Docker image. A day read in the evening in Chicago is that day, not the next.
public static class ReaderClock
{
    public const int MaxZoneLength = 64;

    public static TimeZoneInfo Zone(string? id) =>
        !string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Local;

    public static bool IsZone(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= MaxZoneLength && TimeZoneInfo.TryFindSystemTimeZoneById(id, out _);

    public static DateOnly Today(string? id) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone(id)).DateTime);

    public static DateTimeOffset Local(DateTimeOffset moment, string? id) => TimeZoneInfo.ConvertTime(moment, Zone(id));

    // Every zone a reader can choose, by its IANA name.
    public static IReadOnlyList<string> Zones { get; } = [.. TimeZoneInfo.GetSystemTimeZones().Select(zone => zone.HasIanaId ? zone.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id).Distinct().Order(StringComparer.Ordinal)];
}

// The signed-in reader's clock, for pages: their zone looked up once for the page.
public sealed class ReaderTime(ShelfReader reader, IDbContextFactory<ShelfDb> factory)
{
    private string? zone;
    private bool loaded;

    public string? ZoneId
    {
        get
        {
            if (!loaded)
            {
                using var db = factory.CreateDbContext();
                zone = reader.Id is int id ? db.Readers.AsNoTracking().Where(item => item.Id == id).Select(item => item.TimeZone).FirstOrDefault() : null;
                loaded = true;
            }

            return zone;
        }
    }

    public DateOnly Today => ReaderClock.Today(ZoneId);

    public DateTimeOffset Local(DateTimeOffset moment) => ReaderClock.Local(moment, ZoneId);

    // After the reader chooses another zone.
    public void Forget() => loaded = false;
}

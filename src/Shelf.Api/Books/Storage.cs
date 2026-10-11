using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

public sealed record StorageUse(int ReaderId, string Name, long Ebooks, long Audio, long Covers, long Podcasts, int? LimitMegabytes)
{
    public long Total => Ebooks + Audio + Covers + Podcasts;
}

public sealed record StorageReport(StorageUse[] Readers, long Shared, long? Free, long? Size);

// The space each reader's files take, by kind, and the limit an admin may set on it. The files themselves are the
// measure, so what is counted is what is on the disk.
public sealed class Storage(EbookStore ebooks, AudioStore audio, CoverStore covers, PodcastService podcasts)
{
    public static long Megabytes(int megabytes) => megabytes * 1024L * 1024;

    public async Task<StorageReport> ReportAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var readers = await db.Readers.AsNoTracking().Select(reader => new { reader.Id, reader.Name, reader.StorageLimitMegabytes }).ToListAsync(cancellationToken);
        var uses = new List<StorageUse>();
        foreach (var reader in readers.OrderBy(reader => reader.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var (ebook, recordings, pictures, shows) = await MeasureAsync(db, reader.Id, cancellationToken);
            uses.Add(new StorageUse(reader.Id, reader.Name, ebook, recordings, pictures, shows, reader.StorageLimitMegabytes));
        }

        // KEPUBs made for Kobos, held uploads, and the like: the shelf's, not a reader's.
        var counted = uses.Sum(use => use.Total);
        var all = Folder(ebooks.Root) + Folder(audio.Root) + Folder(covers.Root);
        long? free = null, size = null;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(audio.Root))!);
            (free, size) = (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }

        return new StorageReport([.. uses], Math.Max(0, all - counted), free, size);
    }

    public async Task<long> UsedAsync(ShelfDb db, int readerId, CancellationToken cancellationToken = default)
    {
        var (ebook, recordings, pictures, shows) = await MeasureAsync(db, readerId, cancellationToken);
        return ebook + recordings + pictures + shows;
    }

    // Whether adding a file of this size keeps the reader within their limit; always so with none.
    public async Task<bool> FitsAsync(ShelfDb db, int readerId, long adding, CancellationToken cancellationToken = default)
    {
        var limit = await db.Readers.AsNoTracking().Where(reader => reader.Id == readerId).Select(reader => reader.StorageLimitMegabytes).FirstOrDefaultAsync(cancellationToken);
        return limit is not int megabytes || await UsedAsync(db, readerId, cancellationToken) + adding <= Megabytes(megabytes);
    }

    public static async Task SetLimitAsync(ShelfDb db, int readerId, int? megabytes, CancellationToken cancellationToken = default) =>
        await db.Readers.Where(reader => reader.Id == readerId)
            .ExecuteUpdateAsync(set => set.SetProperty(reader => reader.StorageLimitMegabytes, megabytes is int given && given > 0 ? Math.Min(given, 10_000_000) : null), cancellationToken);

    public long EbookSize(string? storedName) => ebooks.OpenPath(storedName) is { } path ? new FileInfo(path).Length : 0;

    public long AudioSize(string? storedName) => audio.Size(storedName);

    private async Task<(long Ebooks, long Audio, long Covers, long Podcasts)> MeasureAsync(ShelfDb db, int readerId, CancellationToken cancellationToken)
    {
        var files = await db.Books.IgnoreQueryFilters().AsNoTracking().Where(book => book.OwnerId == readerId)
            .Select(book => new { book.EbookStoredName, book.AudioStoredName, book.CoverImage }).ToListAsync(cancellationToken);
        var shows = await db.Podcasts.IgnoreQueryFilters().AsNoTracking().Where(podcast => podcast.OwnerId == readerId).Select(podcast => podcast.Id).ToListAsync(cancellationToken);
        return (
            files.Sum(file => EbookSize(file.EbookStoredName)),
            files.Sum(file => audio.Size(file.AudioStoredName)),
            files.Sum(file => covers.OpenPath(file.CoverImage) is { } path ? new FileInfo(path).Length : 0),
            shows.Sum(id => Folder(Path.Combine(podcasts.Root, id.ToString(CultureInfo.InvariantCulture)))));
    }

    private static long Folder(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

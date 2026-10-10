using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

public enum UploadKind
{
    Ebook,
    Audio,
}

// An upload that turned out to be a file already on another of the reader's books. It waits in the store's held
// folder, on no book, until the reader keeps both or lets it go; the sweep removes it a day later otherwise.
public sealed record HeldUpload(int ReaderId, int BookId, UploadKind Kind, string StoredName, string FileName, int SameAsId);

public sealed record SameFile(int Id, string Title);

public static class Duplicates
{
    private static readonly TimeSpan HoldFor = TimeSpan.FromHours(1);

    // Another of this reader's books with exactly these bytes. Sizes are compared first, so only a file of the
    // same length is ever fingerprinted, and that fingerprint is kept for next time.
    public static async Task<SameFile?> FindEbookAsync(ShelfDb db, EbookStore store, int bookId, string storedName, CancellationToken cancellationToken)
    {
        var path = store.OpenPath(storedName);
        if (path is null)
        {
            return null;
        }

        var size = new FileInfo(path).Length;
        var hash = await store.HashAsync(storedName, cancellationToken);
        var others = await db.Books.AsNoTracking()
            .Where(book => book.Id != bookId && book.EbookStoredName != null)
            .Select(book => new { book.Id, book.Title, book.EbookStoredName })
            .ToListAsync(cancellationToken);
        foreach (var other in others)
        {
            if (store.OpenPath(other.EbookStoredName) is { } otherPath
                && new FileInfo(otherPath).Length == size
                && await store.HashAsync(other.EbookStoredName, cancellationToken) == hash)
            {
                return new SameFile(other.Id, other.Title);
            }
        }

        return null;
    }

    public static async Task<SameFile?> FindAudioAsync(ShelfDb db, AudioStore store, int bookId, string storedName, CancellationToken cancellationToken)
    {
        var size = store.Size(storedName);
        if (size == 0)
        {
            return null;
        }

        string? hash = null;
        var others = await db.Books.AsNoTracking()
            .Where(book => book.Id != bookId && book.AudioStoredName != null)
            .Select(book => new { book.Id, book.Title, book.AudioStoredName })
            .ToListAsync(cancellationToken);
        foreach (var other in others)
        {
            if (store.Size(other.AudioStoredName) != size)
            {
                continue;
            }

            hash ??= await store.HashAsync(storedName, cancellationToken);
            if (await store.HashAsync(other.AudioStoredName, cancellationToken) == hash)
            {
                return new SameFile(other.Id, other.Title);
            }
        }

        return null;
    }

    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider provider) =>
        provider.CreateProtector("Shelf.Uploads.Held").ToTimeLimitedDataProtector();

    public static string Hold(IDataProtectionProvider provider, HeldUpload held) =>
        Protector(provider).Protect(
            string.Join('\n', held.ReaderId, held.BookId, held.Kind, held.StoredName, held.FileName, held.SameAsId),
            HoldFor);

    // The held upload a token names, if it is this reader's, for this book, and still in time.
    public static HeldUpload? Read(IDataProtectionProvider provider, string? token, int readerId, int bookId)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        try
        {
            var parts = Protector(provider).Unprotect(token).Split('\n');
            if (parts.Length != 6
                || !int.TryParse(parts[0], out var reader) || reader != readerId
                || !int.TryParse(parts[1], out var book) || book != bookId
                || !Enum.TryParse<UploadKind>(parts[2], out var kind)
                || !int.TryParse(parts[5], out var sameAs))
            {
                return null;
            }

            return new HeldUpload(reader, book, kind, parts[3], parts[4], sameAs);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    // Held uploads nobody decided on, once they are a day old. Only the held folders are ever looked at.
    public static int Sweep(EbookStore ebooks, AudioStore audio, DateTimeOffset now)
    {
        var cutoff = (now - TimeSpan.FromDays(1)).UtcDateTime;
        var removed = 0;
        foreach (var held in new[] { ebooks.HeldRoot, audio.HeldRoot })
        {
            if (!Directory.Exists(held))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFileSystemEntries(held))
            {
                if (Directory.GetLastWriteTimeUtc(path) >= cutoff)
                {
                    continue;
                }

                try
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }
                    else
                    {
                        File.Delete(path);
                    }

                    removed++;
                }
                catch (IOException)
                {
                }
            }
        }

        return removed;
    }
}

// Every few hours, clears held uploads nobody decided on; at the start, also finds the covers inside older e-books.
public sealed class FileSweep(EbookStore ebooks, AudioStore audio, IServiceScopeFactory scopes, ILogger<FileSweep> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await Covers.BackfillAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), ebooks, stoppingToken);
        }
        catch (Exception ex) when (ex is IOException or Microsoft.EntityFrameworkCore.DbUpdateException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not look for covers inside e-books.");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        do
        {
            try
            {
                var removed = Duplicates.Sweep(ebooks, audio, DateTimeOffset.UtcNow);
                using (var scope = scopes.CreateScope())
                {
                    await Readers.Audit.TrimAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), stoppingToken);
                }

                if (removed > 0)
                {
                    logger.LogInformation("Removed {Count} stored files no book uses.", removed);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or Microsoft.EntityFrameworkCore.DbUpdateException)
            {
                logger.LogWarning(ex, "Could not sweep stored files.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

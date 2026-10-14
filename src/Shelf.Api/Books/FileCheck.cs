using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Books;

public enum FileKind
{
    Ebook,
    Audio,
    Cover,
    Photo,
}

public enum FileProblem
{
    // A book names it, but it is not there.
    Missing,

    // Its bytes are not what they were when first seen. Stored files are never written again, so this is damage.
    Changed,

    // There, but could not be read.
    Unreadable,
}

// The server's own record of one stored file: its SHA-256 when first seen, and how it stood when last looked at.
public sealed class FileRecord
{
    public int Id { get; set; }
    public FileKind Kind { get; set; }
    public required string StoredName { get; set; }
    public string? Sha256 { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
    public FileProblem? Problem { get; set; }
    public DateTimeOffset? ProblemSince { get; set; }
}

public sealed record FileIssue(int BookId, string Title, string Author, FileKind Kind, FileProblem Problem, DateTimeOffset Since);

// With the newest backup a restore drill passed, and whether it carries the e-books and audiobooks or only covers.
public sealed record FileHealth(DateTimeOffset? CheckedAt, int Files, bool Running, FileIssue[] Problems, DateTimeOffset? LastGoodBackup, bool BackupHasFiles);

public sealed record FileCheckResult(int Checked, int Missing, int Changed, int Unreadable);

// Every stored file a book, a picture, or the trash still uses, read through and checked against what it was.
public static class FileCheck
{
    public static async Task<FileCheckResult> RunAsync(ShelfDb db, EbookStore ebooks, AudioStore audio, CoverStore covers, CancellationToken cancellationToken = default)
    {
        var books = db.Books.IgnoreQueryFilters().AsNoTracking();
        var used = new HashSet<(FileKind, string)>();
        used.UnionWith((await books.Where(book => book.EbookStoredName != null).Select(book => book.EbookStoredName!).ToListAsync(cancellationToken)).Select(name => (FileKind.Ebook, name)));
        used.UnionWith((await books.Where(book => book.AudioStoredName != null).Select(book => book.AudioStoredName!).ToListAsync(cancellationToken)).Select(name => (FileKind.Audio, name)));
        used.UnionWith((await books.Where(book => book.CoverImage != null).Select(book => book.CoverImage!).ToListAsync(cancellationToken)).Select(name => (FileKind.Cover, name)));
        used.UnionWith((await db.CopyPhotos.IgnoreQueryFilters().AsNoTracking().Select(photo => photo.StoredName).ToListAsync(cancellationToken)).Select(name => (FileKind.Photo, name)));
        foreach (var line in await Trash.KeptFilesAsync(db, cancellationToken))
        {
            var kind = line[..2] switch { "e:" => FileKind.Ebook, "a:" => FileKind.Audio, "c:" => FileKind.Cover, _ => FileKind.Photo };
            used.Add((kind, line[2..]));
        }

        var records = await db.FileRecords.ToDictionaryAsync(record => (record.Kind, record.StoredName), cancellationToken);
        var (missing, changed, unreadable, done) = (0, 0, 0, 0);
        foreach (var (kind, name) in used)
        {
            var now = DateTimeOffset.UtcNow;
            FileProblem? problem = null;
            string? hash = null;
            try
            {
                hash = kind switch
                {
                    FileKind.Ebook => await ebooks.HashNowAsync(name, cancellationToken),
                    FileKind.Audio => await audio.HashNowAsync(name, cancellationToken),
                    _ => await HashAsync(covers.OpenPath(name), cancellationToken),
                };
                problem = hash is null ? FileProblem.Missing : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problem = FileProblem.Unreadable;
            }

            if (!records.TryGetValue((kind, name), out var record))
            {
                record = new FileRecord { Kind = kind, StoredName = name, Sha256 = hash, FirstSeenAt = now };
                db.FileRecords.Add(record);
                records[(kind, name)] = record;
            }

            record.Sha256 ??= hash;
            if (problem is null && hash is not null && record.Sha256 != hash)
            {
                problem = FileProblem.Changed;
            }

            if (record.Problem != problem)
            {
                record.ProblemSince = problem is null ? null : now;
            }

            record.Problem = problem;
            record.CheckedAt = now;
            missing += problem == FileProblem.Missing ? 1 : 0;
            changed += problem == FileProblem.Changed ? 1 : 0;
            unreadable += problem == FileProblem.Unreadable ? 1 : 0;
            if (++done % 200 == 0)
            {
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        db.FileRecords.RemoveRange(records.Where(pair => !used.Contains(pair.Key)).Select(pair => pair.Value));
        await db.SaveChangesAsync(cancellationToken);
        return new FileCheckResult(done, missing, changed, unreadable);
    }

    private static async Task<string?> HashAsync(string? path, CancellationToken cancellationToken)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    // The reader's own books with a file that is missing, changed, or unreadable, as the last check found them.
    public static async Task<FileIssue[]> ProblemsAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var bad = await db.FileRecords.AsNoTracking().Where(record => record.Problem != null).ToListAsync(cancellationToken);
        if (bad.Count == 0)
        {
            return [];
        }

        // The reader's books with any file, read once and matched here: a long list of names in the query is slow.
        var byName = bad.ToDictionary(record => (record.Kind, record.StoredName));
        var issues = new List<FileIssue>();
        void Match(int bookId, string title, string author, FileKind kind, string? name)
        {
            if (name is not null && byName.TryGetValue((kind, name), out var record) && record.Problem is { } problem)
            {
                issues.Add(new FileIssue(bookId, title, author, kind, problem, record.ProblemSince ?? record.CheckedAt));
            }
        }

        var books = await db.Books.AsNoTracking()
            .Where(book => book.EbookStoredName != null || book.AudioStoredName != null || book.CoverImage != null)
            .Select(book => new { book.Id, book.Title, book.Author, book.EbookStoredName, book.AudioStoredName, book.CoverImage })
            .ToListAsync(cancellationToken);
        foreach (var book in books)
        {
            Match(book.Id, book.Title, book.Author, FileKind.Ebook, book.EbookStoredName);
            Match(book.Id, book.Title, book.Author, FileKind.Audio, book.AudioStoredName);
            Match(book.Id, book.Title, book.Author, FileKind.Cover, book.CoverImage);
        }

        if (bad.Any(record => record.Kind == FileKind.Photo))
        {
            var photos = await db.CopyPhotos.AsNoTracking().Select(photo => new { photo.BookId, photo.StoredName, photo.Book!.Title, photo.Book.Author }).ToListAsync(cancellationToken);
            foreach (var photo in photos)
            {
                Match(photo.BookId, photo.Title, photo.Author, FileKind.Photo, photo.StoredName);
            }
        }

        return [.. issues.OrderBy(issue => issue.Title, StringComparer.CurrentCultureIgnoreCase)];
    }

    public static async Task<FileHealth> HealthAsync(ShelfDb db, FileChecker checker, BackupCheck backups, CancellationToken cancellationToken = default)
    {
        var good = backups.LastGood();
        var checkedAt = await LastRunAsync(db, cancellationToken);
        var files = await db.FileRecords.CountAsync(cancellationToken);
        return new FileHealth(checkedAt, files, checker.Running, await ProblemsAsync(db, cancellationToken), good?.CheckedAt, good?.HasFiles == true);
    }

    // Every record a check keeps is stamped in each run, so the first one says when the last run was.
    public static Task<DateTimeOffset?> LastRunAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        db.FileRecords.AsNoTracking().OrderBy(record => record.Id).Select(record => (DateTimeOffset?)record.CheckedAt).FirstOrDefaultAsync(cancellationToken);

    public static void Map(RouteGroupBuilder books)
    {
        books.MapGet("/files/health", async (ShelfDb db, FileChecker checker, BackupCheck backups, CancellationToken cancellationToken) =>
            TypedResults.Ok(await HealthAsync(db, checker, backups, cancellationToken))).WithTags("Files");

        // Every reader's files are read, so only an admin starts a check out of turn.
        books.MapPost("/files/check", async Task<Results<Accepted, ForbidHttpResult>> (ShelfDb db, FileChecker checker, CancellationToken cancellationToken) =>
        {
            if (!await ReaderRules.IsAdminAsync(db, db.ReaderId, cancellationToken))
            {
                return TypedResults.Forbid();
            }

            checker.Nudge();
            return TypedResults.Accepted("/books/files/health");
        }).WithTags("Files");
    }
}

// Checks every stored file once a week (FileCheck:Days; 0 turns it off), and when an admin asks.
public sealed class FileChecker(IServiceScopeFactory scopes, EbookStore ebooks, AudioStore audio, CoverStore covers, IConfiguration configuration, ILogger<FileChecker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _nudge = new(0, 1);

    public bool Running { get; private set; }

    public void Nudge()
    {
        if (_nudge.CurrentCount == 0)
        {
            _nudge.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var days = configuration.GetValue("FileCheck:Days", 7);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var wait = days <= 0 ? Timeout.InfiniteTimeSpan : await UntilDueAsync(days, stoppingToken);
                await _nudge.WaitAsync(wait, stoppingToken);
                Running = true;
                using var scope = scopes.CreateScope();
                var result = await FileCheck.RunAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), ebooks, audio, covers, stoppingToken);
                logger.LogInformation("Checked {Count} stored files: {Missing} missing, {Changed} changed, {Unreadable} unreadable.", result.Checked, result.Missing, result.Changed, result.Unreadable);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Tried again at the next turn; a check that fails never stops the shelf.
                logger.LogWarning(ex, "Could not check stored files.");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
            finally
            {
                Running = false;
            }
        }
    }

    // From the last check, so a restart does not read every file again.
    private async Task<TimeSpan> UntilDueAsync(int days, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var last = await FileCheck.LastRunAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), cancellationToken);
        if (last is null)
        {
            // Never checked: a while after starting, once the shelf has settled.
            return TimeSpan.FromMinutes(10);
        }

        var due = last.Value.AddDays(days) - DateTimeOffset.UtcNow;
        return due < TimeSpan.Zero ? TimeSpan.Zero : due;
    }
}

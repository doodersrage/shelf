using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// A look through the want list for books with no file, fetching the best match for each.
public sealed class WantedJob(Guid id, int readerId, IReadOnlyList<(int Id, string Title)> books) : IShelfJob
{
    public Guid Id { get; } = id;
    public int ReaderId { get; } = readerId;
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<(int Id, string Title)> Books { get; } = books;
    public ConcurrentDictionary<int, ImportLine> Entries { get; } = new(books.Select(book => KeyValuePair.Create(book.Id, new ImportLine(book.Title, ImportLineState.Waiting, null, book.Id, 0))));
    public bool Finished { get; set; }
    public bool Stopping { get; private set; }

    public string Page => "/wanted";

    private int begun;

    // True the first time only, so a job asked to run twice runs once.
    public bool Begin() => Interlocked.Exchange(ref begun, 1) == 0;

    public string Title(bool finished) => finished ? T("Looked for wanted books") : T("Looking for wanted books");

    public void Stop() => Stopping = true;

    public IReadOnlyList<ImportLine> Lines() => [.. Books.Select(book => Entries[book.Id])];
}

// Finding and fetching books, as Sonarr does for shows: searches the indexers (Jackett or Prowlarr) for a book,
// hands the chosen release to the download client (Deluge or qBittorrent for a torrent, SABnzbd for Usenet), follows
// it there, and brings the finished file onto the book that wanted it. Copies, so a torrent goes on seeding.
public sealed class Fetcher(IServiceScopeFactory scopes, IHttpClientFactory clients, IDataProtectionProvider protection, ShelfJobs register, IConfiguration configuration, ILogger<Fetcher> logger) : BackgroundService
{
    private static readonly string[] EbookExtensions = [".epub", ".azw3", ".mobi", ".azw", ".pdf", ".cbz", ".cbr", ".cb7"];
    private static readonly string[] AudioExtensions = [".m4b", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".flac", ".wav"];

    // Following and cancelling take turns, so a grab being cancelled is not moved on by a look at it begun just before.
    private readonly SemaphoreSlim turn = new(1, 1);

    private IDataProtector Protector => protection.CreateProtector("Shelf.Fetch");

    public string Seal(string secret) => Protector.Protect(secret);

    public string? Open(string? sealedSecret)
    {
        if (string.IsNullOrEmpty(sealedSecret))
        {
            return null;
        }

        try
        {
            return Protector.Unprotect(sealedSecret);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private HttpClient Http => clients.CreateClient(ProwlarrClient.ClientName);

    public static bool Ready(FetchSetting settings) =>
        settings.Enabled && !string.IsNullOrWhiteSpace(settings.IndexerUrl) && (!string.IsNullOrWhiteSpace(settings.TorrentUrl) || !string.IsNullOrWhiteSpace(settings.SabUrl));

    // The releases for a book, best first, each weighed for whether it is that book.
    public async Task<IReadOnlyList<ScoredRelease>> SearchAsync(FetchSetting settings, string title, string author, FetchKind kind, CancellationToken cancellationToken)
    {
        if (!Ready(settings) || Open(settings.SealedIndexerKey) is not { } key)
        {
            throw new FetchProblem(T("Finding books is not set up. An admin sets it up under Downloads."));
        }

        var releases = await Indexers.SearchAsync(Http, settings, key, FetchRules.Search(title, author), kind, cancellationToken);
        // A release for Usenet needs SABnzbd, and one for torrents a torrent client.
        var usable = releases.Where(release => release.Protocol == "usenet" ? !string.IsNullOrWhiteSpace(settings.SabUrl) : !string.IsNullOrWhiteSpace(settings.TorrentUrl));
        return FetchRules.Rank(usable, title, author, kind, settings);
    }

    // Sends a release to its download client for a book; the grab is followed from then on.
    public async Task<FetchGrab> GrabAsync(ShelfDb db, FetchSetting settings, Book book, FetchKind kind, Release release, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var grab = new FetchGrab
        {
            ReaderId = db.ReaderId,
            BookId = book.Id,
            Kind = kind,
            Title = release.Title.Length > 500 ? release.Title[..500] : release.Title,
            Indexer = release.Indexer is { Length: > 200 } indexer ? indexer[..200] : release.Indexer,
            Protocol = release.Protocol,
            State = GrabState.Sent,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.FetchGrabs.Add(grab);
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            var (file, magnet) = await ProwlarrClient.FetchAsync(Http, release, cancellationToken);
            if (release.Protocol == "usenet")
            {
                grab.DownloadId = await SabClient.AddAsync(Http, settings.SabUrl!, Open(settings.SealedSabKey) ?? "", settings.Category, grab.Id, file ?? throw new FetchProblem(T("The release has no file to download.")), cancellationToken);
            }
            else if (settings.TorrentKind == TorrentClientKind.Deluge)
            {
                grab.DownloadId = await DelugeClient.AddAsync(Http, settings.TorrentUrl!, Open(settings.SealedTorrentPassword), settings.Category, grab.Id, file, magnet, cancellationToken);
            }
            else
            {
                await QbitClient.AddAsync(Http, settings.TorrentUrl!, settings.TorrentUser, Open(settings.SealedTorrentPassword), settings.Category, grab.Id, file, magnet, cancellationToken);
                grab.DownloadId = QbitClient.Tag(grab.Id);
            }

            grab.State = GrabState.Downloading;
        }
        catch (Exception ex) when (ex is FetchProblem or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            (grab.State, grab.Problem) = (GrabState.Failed, ex is FetchProblem ? ex.Message : T("The download client did not answer."));
        }

        grab.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return grab;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(configuration.GetValue("Fetch:PollSeconds", 20)));
        do
        {
            try
            {
                await FollowAsync(stoppingToken);
                await WantedWhenDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Following downloads failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // Each download under way: how far it has got, and, once finished, its file onto the book.
    public async Task FollowAsync(CancellationToken cancellationToken)
    {
        await turn.WaitAsync(cancellationToken);
        try
        {
            await FollowEachAsync(cancellationToken);
        }
        finally
        {
            turn.Release();
        }
    }

    private async Task FollowEachAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var settings = await FetchRules.SettingsAsync(db, cancellationToken);
        var open = await db.FetchGrabs.Where(grab => grab.State == GrabState.Sent || grab.State == GrabState.Downloading).ToListAsync(cancellationToken);
        foreach (var grab in open)
        {
            DownloadStatus status;
            try
            {
                status = grab.Protocol == "usenet"
                    ? await SabClient.StatusAsync(Http, settings.SabUrl ?? "", Open(settings.SealedSabKey) ?? "", grab.DownloadId ?? "", cancellationToken)
                    : settings.TorrentKind == TorrentClientKind.Deluge
                        ? await DelugeClient.StatusAsync(Http, settings.TorrentUrl ?? "", Open(settings.SealedTorrentPassword), grab.DownloadId ?? "", cancellationToken)
                        : await QbitClient.StatusAsync(Http, settings.TorrentUrl ?? "", settings.TorrentUser, Open(settings.SealedTorrentPassword), grab.Id, cancellationToken);
            }
            catch (Exception ex) when (ex is FetchProblem or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
            {
                // The client is away for now; it is asked again next time.
                logger.LogDebug(ex, "Could not ask after download {Grab}.", grab.Id);
                continue;
            }

            grab.UpdatedAt = DateTimeOffset.UtcNow;
            if (!status.Found)
            {
                // Gone from the client: removed there by hand, or never arrived. Left a day before giving up.
                if (grab.CreatedAt < DateTimeOffset.UtcNow.AddDays(-1))
                {
                    (grab.State, grab.Problem) = (GrabState.Failed, T("The download client no longer has it."));
                }
            }
            else if (status.Failed)
            {
                (grab.State, grab.Problem) = (GrabState.Failed, status.Problem);
            }
            else if (status.Finished && status.Path is { } path)
            {
                (grab.State, grab.Progress, grab.Path) = (GrabState.Importing, 1, FetchRules.MapPath(path, settings));
                await db.SaveChangesAsync(cancellationToken);
                await ImportAsync(grab.Id, cancellationToken);
                continue;
            }
            else
            {
                (grab.State, grab.Progress) = (GrabState.Downloading, Math.Clamp(status.Progress, 0, 1));
            }

            await db.SaveChangesAsync(cancellationToken);
        }
    }

    // A reader's download stopped before it finishes, such as one stalled: taken out of the download client with what
    // it has so far. The book stays wanted, but is not looked for again; it can still be searched for from its page.
    // False if it is not the reader's, or is past stopping.
    public async Task<bool> CancelAsync(ShelfDb db, int grabId, CancellationToken cancellationToken)
    {
        await turn.WaitAsync(cancellationToken);
        try
        {
            var grab = await db.FetchGrabs.FirstOrDefaultAsync(item => item.Id == grabId && item.ReaderId == db.ReaderId, cancellationToken);
            if (grab is null || grab.State is not (GrabState.Sent or GrabState.Downloading))
            {
                return false;
            }

            var settings = await FetchRules.SettingsAsync(db, cancellationToken);
            string? problem = null;
            try
            {
                if (grab.Protocol == "usenet")
                {
                    if (grab.DownloadId is { } id && settings.SabUrl is { } sab)
                    {
                        await SabClient.RemoveAsync(Http, sab, Open(settings.SealedSabKey) ?? "", id, cancellationToken);
                    }
                }
                else if (settings.TorrentUrl is { } torrents)
                {
                    if (settings.TorrentKind == TorrentClientKind.QBittorrent)
                    {
                        await QbitClient.RemoveAsync(Http, torrents, settings.TorrentUser, Open(settings.SealedTorrentPassword), grab.Id, cancellationToken);
                    }
                    else if (grab.DownloadId is { } hash)
                    {
                        await DelugeClient.RemoveAsync(Http, torrents, Open(settings.SealedTorrentPassword), hash, cancellationToken);
                    }
                }
            }
            catch (Exception ex) when (ex is FetchProblem or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
            {
                // Cancelled all the same; what the client has is left for removing there by hand.
                logger.LogWarning(ex, "Could not take download {Grab} out of the download client.", grab.Id);
                problem = T("The download client did not answer, so it may still have the download.");
            }

            (grab.State, grab.Problem, grab.UpdatedAt) = (GrabState.Cancelled, problem, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        finally
        {
            turn.Release();
        }
    }

    // The finished download onto its book: the best e-book file in it, or every audio file in it as the recording.
    public async Task ImportAsync(int grabId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var grab = await db.FetchGrabs.FirstAsync(item => item.Id == grabId, cancellationToken);
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(grab.ReaderId);
        var settings = await FetchRules.SettingsAsync(db, cancellationToken);
        var book = await db.Books.AsNoTracking().FirstOrDefaultAsync(item => item.Id == grab.BookId, cancellationToken);
        var import = scope.ServiceProvider.GetRequiredService<BookImport>();
        try
        {
            if (book is null)
            {
                throw new FetchProblem(T("The book is no longer on the shelf."));
            }

            var path = grab.Path ?? "";
            var files = File.Exists(path) ? [path] : Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToList() : [];
            if (files.Count == 0)
            {
                throw new FetchProblem(T("Shelf cannot see the finished download at {0}. Set where the download client's files are, as Shelf sees them, under Downloads.", path));
            }

            var known = new FileDetails(book.Title, book.Author, book.Isbn, book.Publisher, book.Year, book.Language);
            ImportedFile result;
            if (grab.Kind == FetchKind.Ebook)
            {
                var liked = FetchRules.Formats(settings.EbookFormats).Select(format => "." + format).Concat(EbookExtensions).Distinct().ToList();
                var chosen = files
                    .Where(file => liked.Contains(Path.GetExtension(file).ToLowerInvariant()) && !Path.GetFileName(file).Contains("sample", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(file => liked.IndexOf(Path.GetExtension(file).ToLowerInvariant()))
                    .ThenByDescending(file => new FileInfo(file).Length)
                    .FirstOrDefault() ?? throw new FetchProblem(T("The download has no e-book in it."));
                await using var stream = File.OpenRead(chosen);
                result = await import.EbookAsync(db, new ImportFile(Path.GetFileName(chosen), stream, stream.Length), keepBoth: false, known, cancellationToken);
            }
            else
            {
                var tracks = files.Where(file => AudioExtensions.Contains(Path.GetExtension(file).ToLowerInvariant())).ToList();
                if (tracks.Count == 0)
                {
                    throw new FetchProblem(T("The download has no audio in it."));
                }

                var streams = tracks.Select(file => new ImportFile(Path.GetFileName(file), File.OpenRead(file), new FileInfo(file).Length)).ToList();
                try
                {
                    result = await import.AudioAsync(db, streams, book.Title, known, keepBoth: false, cancellationToken);
                }
                finally
                {
                    foreach (var stream in streams)
                    {
                        await stream.Content.DisposeAsync();
                    }
                }
            }

            (grab.State, grab.Problem) = result.Outcome switch
            {
                ImportOutcome.Added or ImportOutcome.AddedToExisting when result.BookId == book.Id => (GrabState.Done, (string?)null),
                ImportOutcome.Added or ImportOutcome.AddedToExisting => (GrabState.Done, T("It came in as a book of its own, since this one has a file of that kind already.")),
                ImportOutcome.AlreadyOnShelf => (GrabState.Failed, T("That file is on another book already.")),
                ImportOutcome.TooLarge => (GrabState.Failed, T("The file is larger than the shelf takes.")),
                _ => (GrabState.Failed, T("Shelf could not read the file.")),
            };
        }
        catch (Exception ex) when (ex is FetchProblem or IOException or UnauthorizedAccessException)
        {
            (grab.State, grab.Problem) = (GrabState.Failed, ex is FetchProblem ? ex.Message : T("Shelf could not read the finished download."));
        }

        grab.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    // Once a day, when asked for: each reader's want list, searched, and the best match fetched for each book.
    private async Task WantedWhenDueAsync(CancellationToken cancellationToken)
    {
        List<int> readers;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var settings = await db.FetchSettings.FirstOrDefaultAsync(cancellationToken);
            if (settings is null || !settings.SearchWanted || !Ready(settings) || settings.WantedSearchedAt > DateTimeOffset.UtcNow.AddDays(-1))
            {
                return;
            }

            settings.WantedSearchedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            readers = await db.Readers.AsNoTracking().Select(reader => reader.Id).ToListAsync(cancellationToken);
        }

        foreach (var readerId in readers)
        {
            if (await StartWantedAsync(readerId, cancellationToken) is { } job)
            {
                await RunWantedAsync(job, cancellationToken);
            }
        }
    }

    // A reader's want-to-read books with no file of the kind the shelf fetches, and nothing being fetched for them.
    public async Task<WantedJob?> StartWantedAsync(int readerId, CancellationToken cancellationToken)
    {
        if (register.Of(readerId).OfType<WantedJob>().FirstOrDefault(job => !job.Finished) is { } running)
        {
            return running;
        }

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var settings = await FetchRules.SettingsAsync(db, cancellationToken);
        var cancelled = await CancelledAsync(db, cancellationToken);
        var books = (await WantedAsync(db, settings, cancellationToken)).Where(book => !cancelled.Contains(book.Id)).ToList();
        var most = Math.Clamp(settings.MostPerDay, 1, 200);
        if (books.Count == 0)
        {
            return null;
        }

        var job = new WantedJob(Guid.NewGuid(), readerId, [.. books.Take(most).Select(book => (book.Id, book.Title))]);
        register.Add(job);
        return job;
    }

    public static async Task<List<Book>> WantedAsync(ShelfDb db, FetchSetting settings, CancellationToken cancellationToken = default)
    {
        var busy = await db.FetchGrabs.AsNoTracking()
            .Where(grab => grab.ReaderId == db.ReaderId && grab.State != GrabState.Failed && grab.State != GrabState.Cancelled)
            .Select(grab => grab.BookId).ToListAsync(cancellationToken);
        var books = (await db.Books.AsNoTracking().Where(book => book.Status == BookStatus.Want).ToListAsync(cancellationToken))
            .OrderByDescending(book => book.Queued).ThenBy(book => book.AddedAt);
        return [.. books.Where(book => !busy.Contains(book.Id) && Lacks(book, settings.WantedKind))];
    }

    // Wanted books whose download the reader cancelled, stalled most likely: still wanted, but not looked for again, as
    // the same release would most likely be chosen. They can still be searched for from their pages.
    public static async Task<HashSet<int>> CancelledAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        [.. await db.FetchGrabs.AsNoTracking().Where(grab => grab.ReaderId == db.ReaderId && grab.State == GrabState.Cancelled).Select(grab => grab.BookId).ToListAsync(cancellationToken)];

    // An audiobook is not missing its e-book, nor an e-book its audio: only a book with neither in hand, nor that format, is wanted.
    private static bool Lacks(Book book, FetchKind kind) => kind == FetchKind.Ebook
        ? string.IsNullOrWhiteSpace(book.EbookStoredName) && string.IsNullOrWhiteSpace(book.AudioStoredName) && book.Format != BookFormat.Audiobook
        : string.IsNullOrWhiteSpace(book.AudioStoredName) && string.IsNullOrWhiteSpace(book.EbookStoredName) && book.Format != BookFormat.Ebook;

    public void Run(WantedJob job) => _ = Task.Run(() => RunWantedAsync(job, CancellationToken.None));

    public async Task RunWantedAsync(WantedJob job, CancellationToken cancellationToken)
    {
        if (!job.Begin())
        {
            return;
        }

        var pause = TimeSpan.FromMilliseconds(configuration.GetValue("Fetch:PauseMilliseconds", 3000));
        var first = true;
        foreach (var (id, title) in job.Books)
        {
            if (job.Stopping)
            {
                job.Entries[id] = job.Entries[id] with { State = ImportLineState.Skipped, Note = T("Stopped before this one.") };
                continue;
            }

            if (!first)
            {
                await Task.Delay(pause, cancellationToken);
            }

            first = false;
            job.Entries[id] = job.Entries[id] with { State = ImportLineState.Working };
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(job.ReaderId);
                var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
                var settings = await FetchRules.SettingsAsync(db, cancellationToken);
                var book = await db.Books.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
                if (book is null)
                {
                    job.Entries[id] = job.Entries[id] with { State = ImportLineState.Skipped, Note = T("No longer on the shelf.") };
                    continue;
                }

                var best = (await SearchAsync(settings, book.Title, book.Author, settings.WantedKind, cancellationToken)).FirstOrDefault(item => item.Matches);
                if (best is null)
                {
                    job.Entries[id] = job.Entries[id] with { State = ImportLineState.Skipped, Note = T("Nothing matched.") };
                    continue;
                }

                var grab = await GrabAsync(db, settings, book, settings.WantedKind, best.Release, cancellationToken);
                job.Entries[id] = grab.State == GrabState.Failed
                    ? job.Entries[id] with { State = ImportLineState.Failed, Note = grab.Problem }
                    : job.Entries[id] with { State = ImportLineState.Done, Note = T("Fetching: {0}", best.Release.Title) };
            }
            catch (Exception ex) when (ex is FetchProblem or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                job.Entries[id] = job.Entries[id] with { State = ImportLineState.Failed, Note = ex is FetchProblem ? ex.Message : T("The indexers did not answer.") };
            }
        }

        job.Finished = true;
    }
}

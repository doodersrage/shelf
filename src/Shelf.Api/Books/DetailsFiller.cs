using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public enum DetailsKind
{
    // Every book missing a year, pages, publisher, language, or cover.
    Everything,

    // Only books with no cover at all: no picture of their own, none in their file, and no address for one.
    Covers,

    // The books missing details, asked of each catalog apart; nothing changes until the reader looks the answers over.
    Review,

    // Every book, for details that may have gone stale, looked over the same way.
    ReviewAll,
}

// Filling in the details a shelf lacks, a book at a time, from Open Library (and Google Books after it): run in the
// background, so a shelf of thousands can take its hours without a page held open, and with a pause between books
// so the catalogs are asked politely (Details:PauseMilliseconds).
public sealed class DetailsJob(Guid id, int readerId, DetailsKind kind, IReadOnlyList<(int Id, string Title)> books) : IShelfJob
{
    public Guid Id { get; } = id;
    public int ReaderId { get; } = readerId;
    public DetailsKind Kind { get; } = kind;
    public string Language { get; } = Localization.Words.Current;
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<(int Id, string Title)> Books { get; } = books;
    public ConcurrentDictionary<int, ImportLine> Entries { get; } = new(books.Select(book => KeyValuePair.Create(book.Id, new ImportLine(book.Title, ImportLineState.Waiting, null, book.Id, 0))));
    public bool Finished { get; set; }
    public bool Stopping { get; private set; }

    // A review's answers wait on Tidy up; a fill's work is told on Activity.
    public string Page => Kind is DetailsKind.Review or DetailsKind.ReviewAll ? "/tidy" : "/activity";

    public string Title(bool finished) => (Kind, finished) switch
    {
        (DetailsKind.Covers, false) => T("Finding covers"),
        (DetailsKind.Covers, true) => T("Covers looked for"),
        (DetailsKind.Review or DetailsKind.ReviewAll, false) => T("Asking the catalogs"),
        (DetailsKind.Review or DetailsKind.ReviewAll, true) => T("Details ready to look over"),
        (_, false) => T("Filling in details"),
        _ => T("Details filled in"),
    };

    public void Stop() => Stopping = true;

    public IReadOnlyList<ImportLine> Lines() => [.. Books.Select(book => Entries[book.Id])];
}

public sealed class DetailsFiller(IServiceScopeFactory scopes, ShelfJobs register, IConfiguration configuration, ILogger<DetailsFiller> logger) : BackgroundService
{
    private readonly Channel<DetailsJob> _queue = Channel.CreateUnbounded<DetailsJob>();

    private TimeSpan Pause => TimeSpan.FromMilliseconds(configuration.GetValue("Details:PauseMilliseconds", 1200));

    public static bool NeedsDetails(Book book) =>
        book.Year is null
        || book.Pages is null
        || string.IsNullOrWhiteSpace(book.Publisher)
        || string.IsNullOrWhiteSpace(book.Language)
        || string.IsNullOrWhiteSpace(book.CoverUrl);

    // No cover from anywhere: not uploaded, not inside its e-book (noted when the file came), and no address.
    public static bool LacksCover(Book book) => !Covers.HasCover(book);

    public async Task<IReadOnlyList<Book>> WantingAsync(ShelfDb db, DetailsKind kind, CancellationToken cancellationToken = default)
    {
        var books = await db.Books.AsNoTracking().OrderBy(book => book.Title).ToListAsync(cancellationToken);
        return kind switch
        {
            DetailsKind.Covers => [.. books.Where(LacksCover)],
            DetailsKind.ReviewAll => books,
            _ => [.. books.Where(NeedsDetails)],
        };
    }

    // One at a time for a reader: asking again while one runs gives back the one running.
    public async Task<DetailsJob?> StartAsync(ShelfDb db, DetailsKind kind, CancellationToken cancellationToken = default)
    {
        if (register.Of(db.ReaderId).OfType<DetailsJob>().FirstOrDefault(job => !job.Finished) is { } running)
        {
            return running;
        }

        var books = await WantingAsync(db, kind, cancellationToken);
        if (books.Count == 0)
        {
            return null;
        }

        var job = new DetailsJob(Guid.NewGuid(), db.ReaderId, kind, [.. books.Select(book => (book.Id, book.Title))]);
        register.Add(job);
        _queue.Writer.TryWrite(job);
        return job;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunAsync(job, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // The job ends, so the reader can start another, and the next one in the queue still runs.
                logger.LogError(ex, "Filling in details stopped part way.");
                job.Finished = true;
            }
        }
    }

    // The whole job, for the queue or a test that waits for it.
    public async Task RunAsync(DetailsJob job, CancellationToken cancellationToken)
    {
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(job.Language);
        var reviewing = job.Kind is DetailsKind.Review or DetailsKind.ReviewAll;

        // What a fill changes is gathered under one change, to be undone at once; a review changes nothing yet.
        int? batchId = null;
        if (!reviewing)
        {
            using var start = scopes.CreateScope();
            start.ServiceProvider.GetRequiredService<ShelfReader>().Use(job.ReaderId);
            batchId = (await History.StartAsync(start.ServiceProvider.GetRequiredService<ShelfDb>(), BatchKind.Details, job.Title(finished: true), cancellationToken)).Id;
        }

        using var _ = History.Use(batchId);
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
                await Task.Delay(Pause, cancellationToken);
            }

            first = false;
            job.Entries[id] = job.Entries[id] with { State = ImportLineState.Working };
            try
            {
                job.Entries[id] = await FillAsync(job, id, title, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DbUpdateException or InvalidOperationException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                logger.LogInformation(ex, "Could not fill in the details of book {Book}.", id);
                job.Entries[id] = job.Entries[id] with { State = ImportLineState.Failed, Note = T("The catalogs did not answer.") };
            }
        }

        job.Finished = true;
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(job.ReaderId);
        var filled = job.Entries.Values.Count(line => line.State == ImportLineState.Done);
        await Audit.NoteAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), reviewing ? Say("Asked the catalogs about details") : Say("Filled in details"), detail: Localization.Counts.Books(filled), cancellationToken: cancellationToken);
    }

    private async Task<ImportLine> FillAsync(DetailsJob job, int id, string title, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(job.ReaderId);
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var lookup = scope.ServiceProvider.GetRequiredService<IBookLookup>();
        var book = await db.Books.WithDetails().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (book is null)
        {
            return new ImportLine(title, ImportLineState.Skipped, T("No longer on the shelf."), null, 0);
        }

        if (job.Kind is DetailsKind.Review or DetailsKind.ReviewAll)
        {
            var (openLibrary, google) = await scope.ServiceProvider.GetRequiredService<ICatalogSources>().BothAsync(book.Isbn, book.Title, book.Author, cancellationToken);
            if (openLibrary is null && google is null)
            {
                return new ImportLine(title, ImportLineState.Skipped, T("Nothing matched."), id, 0);
            }

            var proposed = await DetailReview.ProposeAsync(db, book, openLibrary, google, cancellationToken);
            return proposed == 0
                ? new ImportLine(title, ImportLineState.Skipped, T("Nothing new turned up."), id, 0)
                : new ImportLine(title, ImportLineState.Done, proposed == 1 ? T("1 change to look over") : T("{0} changes to look over", proposed), id, 0);
        }

        var match = await lookup.FindAsync(book.Isbn, book.Title, book.Author, cancellationToken);
        if (match is null)
        {
            // Out of reach is not the same as unknown: failed, so it's worth filling in again later.
            return lookup.CouldNotAsk
                ? new ImportLine(title, ImportLineState.Failed, T("The catalogs did not answer."), id, 0)
                : new ImportLine(title, ImportLineState.Skipped, T("Nothing matched."), id, 0);
        }

        var before = (book.Year, book.Pages, book.Publisher, book.Language, book.CoverUrl, book.Isbn);
        var tags = book.Tags.Select(tag => tag.Name).ToList();
        if (job.Kind == DetailsKind.Covers)
        {
            // Only the cover: nothing else of the book changes.
            if (match.CoverUrl is not { Length: > 0 and <= 500 } cover || !BookRules.IsCoverUrl(cover))
            {
                return new ImportLine(title, ImportLineState.Skipped, T("No cover found."), id, 0);
            }

            book.CoverUrl = cover.Trim();
        }
        else if (!BookRules.FillEmpty(book, match, tags))
        {
            return new ImportLine(title, ImportLineState.Skipped, T("Nothing new turned up."), id, 0);
        }
        else
        {
            await BookRules.SyncTagsAsync(db, book, tags, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        var filled = new List<string>();
        if (before.Year is null && book.Year is not null) filled.Add(T("year"));
        if (before.Pages is null && book.Pages is not null) filled.Add(T("pages"));
        if (string.IsNullOrWhiteSpace(before.Publisher) && !string.IsNullOrWhiteSpace(book.Publisher)) filled.Add(T("publisher"));
        if (string.IsNullOrWhiteSpace(before.Language) && !string.IsNullOrWhiteSpace(book.Language)) filled.Add(T("language"));
        if (string.IsNullOrWhiteSpace(before.CoverUrl) && !string.IsNullOrWhiteSpace(book.CoverUrl)) filled.Add(T("cover"));
        if (string.IsNullOrWhiteSpace(before.Isbn) && !string.IsNullOrWhiteSpace(book.Isbn)) filled.Add("ISBN");
        return new ImportLine(title, ImportLineState.Done, filled.Count == 0 ? null : T("Filled: {0}", string.Join(", ", filled)), id, 0);
    }
}

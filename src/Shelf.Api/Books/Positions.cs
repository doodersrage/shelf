using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// Where one source last said a reader was in a book: the reader in Shelf, its player, KOReader, a Kobo, an
// Audiobookshelf app. Kept whether or not the book's place moved for it, as a place only moves forward by itself.
public sealed class PositionReport
{
    public const int MaxSourceLength = 40;
    public const int MaxDeviceLength = 80;

    public int Id { get; set; }
    public int ReaderId { get; set; }
    public int BookId { get; set; }
    public required string Source { get; set; }
    public string Device { get; set; } = "";
    public int? EbookChapter { get; set; }
    public string? EbookCfi { get; set; }
    public double? EbookProgress { get; set; }
    public int? AudioTrack { get; set; }
    public int? AudioSeconds { get; set; }
    public DateTimeOffset At { get; set; }

    // Whether the place the book keeps is this one, as it was told.
    public bool Kept { get; set; }
}

// A place chosen by the reader from what a source said, kept as the record of choices for the book.
public sealed class PositionChoiceRow
{
    public int Id { get; set; }
    public int ReaderId { get; set; }
    public int BookId { get; set; }
    public required string Source { get; set; }
    public string Device { get; set; } = "";
    public required string Place { get; set; }
    public required string Before { get; set; }
    public DateTimeOffset At { get; set; }
}

public sealed record PositionView(int Id, string Source, string Device, DateTimeOffset At, string Where, bool Current, bool Kept);

public sealed record PositionsOfBook(string Current, PositionView[] Reports, PositionChoice[] Chosen);

public sealed record PositionChoice(DateTimeOffset At, string Source, string Device, string Place, string Before);

// Where each source says a reader is, beside where Shelf keeps them, and a way to choose: going back is allowed
// here, as the reader chooses it.
public static class Positions
{
    public const int MaxReportsPerBook = 20;

    private static readonly AsyncLocal<(string Source, string? Device)?> From = new();

    // The source of the places kept from here on, until the result is disposed.
    public static IDisposable Reporting(string source, string? device = null)
    {
        var before = From.Value;
        From.Value = (source, device);
        return new Restore(() => From.Value = before);
    }

    public static void Map(RouteGroupBuilder books)
    {
        books.MapGet("/{id:int}/positions", async Task<IResult> (int id, ShelfDb db, AudioStore audio, CancellationToken cancellationToken) =>
            await ForBookAsync(db, id, audio, cancellationToken) is { } found ? TypedResults.Ok(found) : TypedResults.NotFound()).WithTags("Reading");
        books.MapPost("/{id:int}/positions/{reportId:int}/use", async Task<IResult> (int id, int reportId, ShelfDb db, CancellationToken cancellationToken) =>
            await UseAsync(db, id, reportId, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound()).WithTags("Reading");
    }

    // Called as a place is kept: what the source said, and whether the place kept is now that.
    public static async Task NoteAsync(ShelfDb db, int bookId, LoanPlace said, LoanPlace kept, CancellationToken cancellationToken = default)
    {
        var (source, device) = From.Value ?? ("Shelf", null);
        if (said.EbookChapter is null && said.AudioTrack is null && said.AudioSeconds is null && said.EbookProgress is null)
        {
            return;
        }

        device = Cut(device ?? "", PositionReport.MaxDeviceLength);
        source = Cut(source, PositionReport.MaxSourceLength);
        var readerId = db.ReaderId;
        var report = await db.PositionReports.FirstOrDefaultAsync(item => item.ReaderId == readerId && item.BookId == bookId && item.Source == source && item.Device == device, cancellationToken);
        if (report is null)
        {
            report = new PositionReport { ReaderId = readerId, BookId = bookId, Source = source, Device = device };
            db.PositionReports.Add(report);
            var count = await db.PositionReports.CountAsync(item => item.ReaderId == readerId && item.BookId == bookId, cancellationToken);
            if (count >= MaxReportsPerBook)
            {
                // Moments can't be ordered in SQLite, so the oldest is found here.
                var oldest = (await db.PositionReports.Where(item => item.ReaderId == readerId && item.BookId == bookId).ToListAsync(cancellationToken)).MinBy(item => item.At);
                db.PositionReports.Remove(oldest!);
            }
        }

        Fill(report, said, kept);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (db.Entry(report).State == EntityState.Added)
        {
            // The same source told twice at once (a player saving as a book ends, say): the other made the row
            // first, so this one fills it in. The place itself was kept before this.
            foreach (var entry in db.ChangeTracker.Entries<PositionReport>().ToList())
            {
                entry.State = EntityState.Detached;
            }

            if (await db.PositionReports.FirstOrDefaultAsync(item => item.ReaderId == readerId && item.BookId == bookId && item.Source == source && item.Device == device, cancellationToken) is { } made)
            {
                Fill(made, said, kept);
                await db.SaveChangesAsync(cancellationToken);
            }
        }
    }

    private static void Fill(PositionReport report, LoanPlace said, LoanPlace kept)
    {
        (report.EbookChapter, report.EbookCfi, report.EbookProgress) = (said.EbookChapter, said.EbookCfi is { Length: <= 1000 } cfi ? cfi : null, said.EbookProgress);
        (report.AudioTrack, report.AudioSeconds) = (said.AudioTrack, said.AudioSeconds);
        report.At = DateTimeOffset.UtcNow;
        report.Kept = Same(report, kept);
    }

    public static async Task<PositionsOfBook?> ForBookAsync(ShelfDb db, int bookId, AudioStore? audio = null, CancellationToken cancellationToken = default)
    {
        if (await Lending.OpenAsync(db, bookId, cancellationToken) is not { } open)
        {
            return null;
        }

        var place = await Lending.PlaceAsync(db, open, cancellationToken);
        var readerId = db.ReaderId;
        var reports = (await db.PositionReports.AsNoTracking().Where(item => item.ReaderId == readerId && item.BookId == bookId).ToListAsync(cancellationToken))
            .OrderByDescending(item => item.At).ToList();
        var lengths = audio is not null && open.Book.AudioStoredName is { } stored ? audio.Lengths(stored) : [];
        var chosen = await db.PositionChoices.AsNoTracking().Where(item => item.ReaderId == readerId && item.BookId == bookId)
            .OrderByDescending(item => item.Id).Take(10).ToListAsync(cancellationToken);
        return new PositionsOfBook(
            Describe(place.EbookChapter, place.EbookProgress, place.AudioTrack, place.AudioSeconds, lengths),
            [.. reports.Select(report => new PositionView(report.Id, report.Source, report.Device, report.At,
                Describe(report.EbookChapter, report.EbookProgress, report.AudioTrack, report.AudioSeconds, lengths), Same(report, place), report.Kept))],
            [.. chosen.Select(item => new PositionChoice(item.At, item.Source, item.Device, item.Place, item.Before))]);
    }

    // The book's place set to what one source said, back or forward, and the choice noted in the activity log.
    public static async Task<bool> UseAsync(ShelfDb db, int bookId, int reportId, CancellationToken cancellationToken = default)
    {
        var readerId = db.ReaderId;
        var report = await db.PositionReports.AsNoTracking().FirstOrDefaultAsync(item => item.Id == reportId && item.ReaderId == readerId && item.BookId == bookId, cancellationToken);
        if (report is null || await Lending.OpenAsync(db, bookId, cancellationToken) is not { } open)
        {
            return false;
        }

        var before = await Lending.PlaceAsync(db, open, cancellationToken);
        using (Reporting("Shelf", "chosen"))
        {
            await Lending.KeepPlaceAsync(db, open, place =>
            {
                (place.EbookChapter, place.EbookCfi, place.EbookProgress) = (report.EbookChapter ?? place.EbookChapter, report.EbookChapter is null ? place.EbookCfi : report.EbookCfi, report.EbookChapter is null ? place.EbookProgress : report.EbookProgress);
                (place.AudioTrack, place.AudioSeconds) = report.AudioTrack is null && report.AudioSeconds is null ? (place.AudioTrack, place.AudioSeconds) : (report.AudioTrack, report.AudioSeconds);
            }, cancellationToken);
        }

        await db.PositionReports.Where(item => item.ReaderId == readerId && item.BookId == bookId).ExecuteUpdateAsync(set => set.SetProperty(item => item.Kept, item => item.Id == reportId), cancellationToken);
        db.PositionChoices.Add(new PositionChoiceRow
        {
            ReaderId = readerId,
            BookId = bookId,
            Source = report.Source,
            Device = report.Device,
            Place = Describe(report.EbookChapter, report.EbookProgress, report.AudioTrack, report.AudioSeconds, []),
            Before = Describe(before.EbookChapter, before.EbookProgress, before.AudioTrack, before.AudioSeconds, []),
            At = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public static string Describe(int? chapter, double? progress, int? track, int? seconds, IReadOnlyList<double?> lengths)
    {
        var parts = new List<string>();
        if (chapter is int part)
        {
            parts.Add(progress is double share and > 0 ? T("part {0}, {1}% through", part + 1, Math.Round(share * 100)) : T("part {0}", part + 1));
        }

        if (track is not null || seconds is not null)
        {
            var at = lengths.Count > 0 && lengths.All(length => length is not null)
                ? lengths.Take(Math.Clamp(track ?? 0, 0, lengths.Count)).Sum(length => length!.Value) + (seconds ?? 0)
                : seconds ?? 0;
            parts.Add(lengths.Count > 1 && lengths.Any(length => length is null) ? T("track {0}, {1}", (track ?? 0) + 1, ChapterEditing.Clock(seconds ?? 0)) : T("at {0}", ChapterEditing.Clock(at)));
        }

        return parts.Count == 0 ? T("the start") : string.Join(" · ", parts);
    }

    // Who said it, in words: Shelf's own reader and player by name, a device as it calls itself.
    public static string SourceLabel(string source, string device) => (source, device) switch
    {
        ("Shelf", "reader") => T("Shelf's reader"),
        ("Shelf", "player") => T("Shelf's player"),
        ("Shelf", "offline") => T("Reading offline"),
        ("Shelf", "listening offline") => T("Listening offline"),
        ("Shelf", "chosen") => T("Chosen by you"),
        ("Shelf", "") => "Shelf",
        (_, "") => source,
        _ => $"{source} · {device}",
    };

    private static bool Same(PositionReport report, LoanPlace place) =>
        (report.EbookChapter is null || (report.EbookChapter == place.EbookChapter && (report.EbookCfi is null || report.EbookCfi == place.EbookCfi)))
        && (report.AudioTrack is null && report.AudioSeconds is null || ((report.AudioTrack ?? 0) == (place.AudioTrack ?? 0) && Math.Abs((report.AudioSeconds ?? 0) - (place.AudioSeconds ?? 0)) <= 5));

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}

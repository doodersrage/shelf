using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public sealed record DuplicateBook(int Id, string Title, string Author, int? Year, string? Isbn, BookStatus Status, BookFormat? Format, bool HasEbook, bool HasAudio, string? CoverUrl, int Quotes, int Sessions, DateTimeOffset AddedAt);

// Books on one shelf that look like the same book: the same ISBN, or the same title and author.
public sealed record DuplicateGroup(string Reason, DuplicateBook[] Books);

public sealed record MergeRequest(int OtherId);

public enum MergeProblem
{
    None,
    NotFound,
    Same,
    OnLoan,
}

// Two entries for one book become one. The book kept takes what it lacks from the other: empty details, the further
// status and the wider reading dates, tags, notes, quotes, highlights, sessions, time spent, collections, and any
// file of a kind it has none of. A file of a kind both have stays with the book kept, and the other's goes, with
// the bookmarks in its recording.
public static partial class Merging
{
    public static void Map(RouteGroupBuilder books)
    {
        books.MapGet("/duplicates", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await FindAsync(db, cancellationToken)));
        books.MapPost("/{id:int}/merge", Merge);
    }

    public static async Task<DuplicateGroup[]> FindAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var books = await db.Books.AsNoTracking()
            .Select(book => new
            {
                book.Id, book.Title, book.Author, book.Year, book.Isbn, book.Status, book.Format, book.EbookStoredName, book.AudioStoredName,
                book.CoverUrl, book.FileCover, book.CoverImage, book.AddedAt, Quotes = book.Quotes.Count, Sessions = book.Sessions.Count,
            })
            .ToListAsync(cancellationToken);
        var shown = books.ToDictionary(book => book.Id, book => new DuplicateBook(
            book.Id, book.Title, book.Author, book.Year, book.Isbn, book.Status, book.Format, book.EbookStoredName is not null, book.AudioStoredName is not null,
            Covers.For(book.Id, book.CoverUrl, book.FileCover, book.CoverImage), book.Quotes, book.Sessions, book.AddedAt));

        var groups = new List<DuplicateGroup>();
        var seen = new HashSet<string>();
        foreach (var group in books.Where(book => !string.IsNullOrWhiteSpace(book.Isbn)).GroupBy(book => book.Isbn!).Where(group => group.Count() > 1))
        {
            seen.Add(Key(group.Select(book => book.Id)));
            groups.Add(new DuplicateGroup("isbn", group.OrderBy(book => book.AddedAt).Select(book => shown[book.Id]).ToArray()));
        }

        foreach (var group in books.GroupBy(book => (Simple(book.Title), Simple(book.Author))).Where(group => group.Key.Item1.Length > 0 && group.Count() > 1))
        {
            if (seen.Add(Key(group.Select(book => book.Id))))
            {
                groups.Add(new DuplicateGroup("title", group.OrderBy(book => book.AddedAt).Select(book => shown[book.Id]).ToArray()));
            }
        }

        return groups.OrderBy(group => group.Books[0].Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static async Task<MergeProblem> MergeAsync(ShelfDb db, EbookStore ebooks, AudioStore audio, CoverStore covers, int keepId, int otherId, CancellationToken cancellationToken = default)
    {
        if (keepId == otherId)
        {
            return MergeProblem.Same;
        }

        var keep = await db.Books.Include(book => book.Tags).FirstOrDefaultAsync(book => book.Id == keepId, cancellationToken);
        var other = await db.Books.Include(book => book.Tags).FirstOrDefaultAsync(book => book.Id == otherId, cancellationToken);
        if (keep is null || other is null)
        {
            return MergeProblem.NotFound;
        }

        if (other.BorrowerId is not null || other.LoanedTo is not null)
        {
            return MergeProblem.OnLoan;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        FillDetails(keep, other);
        foreach (var tag in other.Tags.Where(tag => keep.Tags.All(mine => mine.Id != tag.Id)).ToList())
        {
            keep.Tags.Add(tag);
        }

        // Files: one of a kind the book kept lacks comes across, with what belongs to it.
        var ebookMoves = keep.EbookStoredName is null && other.EbookStoredName is not null;
        var audioMoves = keep.AudioStoredName is null && other.AudioStoredName is not null;
        var coverMoves = keep.CoverImage is null && other.CoverImage is not null;
        string? dropEbook = null, dropAudio = null, dropCover = null;
        if (ebookMoves)
        {
            (keep.EbookStoredName, keep.EbookFileName, keep.EbookChapter, keep.FileCover, keep.KoreaderDigest) = (other.EbookStoredName, other.EbookFileName, other.EbookChapter, other.FileCover, other.KoreaderDigest);
            await db.OcrScans.Where(scan => scan.BookId == otherId).ExecuteUpdateAsync(set => set.SetProperty(scan => scan.BookId, keepId), cancellationToken);
            await db.BookTexts.Where(text => text.BookId == otherId).ExecuteUpdateAsync(set => set.SetProperty(text => text.BookId, keepId), cancellationToken);
        }
        else
        {
            dropEbook = other.EbookStoredName;
        }

        if (audioMoves)
        {
            (keep.AudioStoredName, keep.AudioFileName, keep.AudioTrack, keep.AudioSeconds) = (other.AudioStoredName, other.AudioFileName, other.AudioTrack, other.AudioSeconds);
        }
        else
        {
            dropAudio = other.AudioStoredName;
        }

        if (coverMoves)
        {
            keep.CoverImage = other.CoverImage;
        }
        else
        {
            dropCover = other.CoverImage;
        }

        // Everything written about the book, by the owner and by anyone it was lent to.
        var keptQuotes = (await db.Quotes.Where(quote => quote.BookId == keepId).Select(quote => quote.Text).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        await db.Quotes.Where(quote => quote.BookId == otherId && !keptQuotes.Contains(quote.Text)).ExecuteUpdateAsync(set => set.SetProperty(quote => quote.BookId, keepId), cancellationToken);
        await db.Highlights.IgnoreQueryFilters().Where(mark => mark.BookId == otherId).ExecuteUpdateAsync(set => set.SetProperty(mark => mark.BookId, keepId), cancellationToken);
        await db.Sessions.IgnoreQueryFilters().Where(session => session.BookId == otherId).ExecuteUpdateAsync(set => set.SetProperty(session => session.BookId, keepId), cancellationToken);
        if (audioMoves || keep.AudioStoredName is null)
        {
            await db.AudioBookmarks.IgnoreQueryFilters().Where(mark => mark.BookId == otherId).ExecuteUpdateAsync(set => set.SetProperty(mark => mark.BookId, keepId), cancellationToken);
        }

        if (ebookMoves)
        {
            var keptDevices = await db.KosyncPlaces.Where(place => place.BookId == keepId).Select(place => place.ReaderId).ToListAsync(cancellationToken);
            await db.KosyncPlaces.Where(place => place.BookId == otherId && !keptDevices.Contains(place.ReaderId)).ExecuteUpdateAsync(set => set.SetProperty(place => place.BookId, keepId), cancellationToken);
        }

        var keptIn = await db.CollectionBooks.IgnoreQueryFilters().Where(entry => entry.BookId == keepId).Select(entry => entry.CollectionId).ToListAsync(cancellationToken);
        await db.CollectionBooks.IgnoreQueryFilters().Where(entry => entry.BookId == otherId && !keptIn.Contains(entry.CollectionId))
            .ExecuteUpdateAsync(set => set.SetProperty(entry => entry.BookId, keepId), cancellationToken);
        await MoveDaysAsync(db, keepId, otherId, cancellationToken);

        // The other book's files left behind go, so the database only names files it still has.
        other.EbookStoredName = ebookMoves ? null : other.EbookStoredName;
        other.AudioStoredName = audioMoves ? null : other.AudioStoredName;
        other.CoverImage = coverMoves ? null : other.CoverImage;
        db.Books.Remove(other);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        ebooks.Delete(dropEbook);
        audio.Delete(dropAudio);
        covers.Delete(dropCover);
        await BookRules.RemoveUnusedTagsAsync(db, cancellationToken);
        await Audit.NoteAsync(db, Say("Merged two entries for one book"), detail: keep.Title, cancellationToken: cancellationToken);
        return MergeProblem.None;
    }

    public static string Describe(MergeProblem problem) => problem switch
    {
        MergeProblem.NotFound => T("Both books have to be on your shelf."),
        MergeProblem.Same => T("Choose two different books."),
        MergeProblem.OnLoan => T("The book to merge away is on loan. Mark it returned first."),
        _ => "",
    };

    private static void FillDetails(Book keep, Book other)
    {
        keep.Subtitle ??= other.Subtitle;
        keep.Year ??= other.Year;
        keep.Isbn ??= other.Isbn;
        keep.Pages ??= other.Pages;
        keep.Publisher ??= other.Publisher;
        keep.Language ??= other.Language;
        keep.Format ??= other.Format;
        keep.Series ??= other.Series;
        keep.SeriesNumber ??= other.SeriesNumber;
        keep.CoverUrl ??= other.CoverUrl;
        keep.Location ??= other.Location;
        keep.AcquiredOn ??= other.AcquiredOn;
        keep.Acquisition ??= other.Acquisition;
        keep.Condition ??= other.Condition;
        keep.Translator ??= other.Translator;
        keep.Narrator ??= other.Narrator;
        keep.OriginalTitle ??= other.OriginalTitle;
        keep.Inscription ??= other.Inscription;
        keep.RecommendedBy ??= other.RecommendedBy;
        keep.Rating ??= other.Rating;
        keep.Loved |= other.Loved;
        keep.Notes = Join(keep.Notes, other.Notes);
        keep.Review = Join(keep.Review, other.Review);

        // The further status, with the earliest start and the latest finish.
        if (Rank(other.Status) > Rank(keep.Status))
        {
            keep.Status = other.Status;
        }

        keep.StartedOn = Earliest(keep.StartedOn, other.StartedOn);
        keep.FinishedOn = Latest(keep.FinishedOn, other.FinishedOn);
        keep.CurrentPage = Math.Max(keep.CurrentPage ?? 0, other.CurrentPage ?? 0) is > 0 and var page ? page : null;
        keep.Queued = keep.Status == BookStatus.Want && (keep.Queued || other.Queued);
        if (other.AddedAt != default && other.AddedAt < keep.AddedAt)
        {
            keep.AddedAt = other.AddedAt;
        }
    }

    // Time on the same day, by the same reader, adds up; other days move across.
    private static async Task MoveDaysAsync(ShelfDb db, int keepId, int otherId, CancellationToken cancellationToken)
    {
        var listened = await db.ListeningDays.IgnoreQueryFilters().Where(day => day.BookId == keepId || day.BookId == otherId).ToListAsync(cancellationToken);
        foreach (var row in listened.Where(day => day.BookId == otherId))
        {
            if (listened.FirstOrDefault(day => day.BookId == keepId && day.ReaderId == row.ReaderId && day.Day == row.Day) is { } into)
            {
                into.Seconds += row.Seconds;
                db.ListeningDays.Remove(row);
            }
            else
            {
                row.BookId = keepId;
            }
        }

        var read = await db.ReadingDays.IgnoreQueryFilters().Where(day => day.BookId == keepId || day.BookId == otherId).ToListAsync(cancellationToken);
        foreach (var row in read.Where(day => day.BookId == otherId))
        {
            if (read.FirstOrDefault(day => day.BookId == keepId && day.ReaderId == row.ReaderId && day.Day == row.Day) is { } into)
            {
                into.Seconds += row.Seconds;
                db.ReadingDays.Remove(row);
            }
            else
            {
                row.BookId = keepId;
            }
        }
    }

    private static int Rank(BookStatus status) => status switch
    {
        BookStatus.Finished => 3,
        BookStatus.Reading => 2,
        BookStatus.Abandoned => 1,
        _ => 0,
    };

    private static DateOnly? Earliest(DateOnly? one, DateOnly? other) => one is null ? other : other is null ? one : one < other ? one : other;

    private static DateOnly? Latest(DateOnly? one, DateOnly? other) => one is null ? other : other is null ? one : one > other ? one : other;

    private static string? Join(string? kept, string? other)
    {
        if (string.IsNullOrWhiteSpace(other) || string.Equals(kept?.Trim(), other.Trim(), StringComparison.Ordinal))
        {
            return kept;
        }

        var joined = string.IsNullOrWhiteSpace(kept) ? other.Trim() : kept.Trim() + "\n\n" + other.Trim();
        return joined.Length > 4000 ? joined[..4000] : joined;
    }

    private static string Key(IEnumerable<int> ids) => string.Join(',', ids.Order());

    private static string Simple(string? text) => Letters().Replace((text ?? "").ToLowerInvariant(), "");

    private static async Task<IResult> Merge(int id, MergeRequest request, ShelfDb db, EbookStore ebooks, AudioStore audio, CoverStore covers, CancellationToken cancellationToken) =>
        await MergeAsync(db, ebooks, audio, covers, id, request.OtherId, cancellationToken) switch
        {
            MergeProblem.None => TypedResults.NoContent(),
            MergeProblem.NotFound => TypedResults.NotFound(),
            var problem => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["OtherId"] = [Describe(problem)] }),
        };

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex Letters();
}

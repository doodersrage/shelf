using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public sealed record DuplicateBook(int Id, string Title, string Author, int? Year, string? Isbn, BookStatus Status, BookFormat? Format, bool HasEbook, bool HasAudio, string? CoverUrl, int Quotes, int Sessions, DateTimeOffset AddedAt);

// Books on one shelf that look like the same book: the same ISBN, the same title and author, or by the same author
// with titles alike but for a subtitle, an edition, or a word or two. Score says how sure, out of 100.
public sealed record DuplicateGroup(string Reason, DuplicateBook[] Books, int Score = 100);

// Two books the reader has said are not the same, so they are not shown together again.
public sealed class NotDuplicate
{
    public int Id { get; set; }
    public int OwnerId { get; set; }
    public int BookId { get; set; }
    public int OtherId { get; set; }
}

public sealed record NotDuplicateRequest(int[] Ids);

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
        books.MapPost("/duplicates/dismiss", async Task<IResult> (NotDuplicateRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            await DismissAsync(db, request.Ids ?? [], cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());
    }

    // Each pair among these books is not the same book.
    public static async Task<bool> DismissAsync(ShelfDb db, IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default)
    {
        var mine = await db.Books.Where(book => ids.Contains(book.Id)).Select(book => book.Id).OrderBy(id => id).ToListAsync(cancellationToken);
        if (mine.Count < 2 || mine.Count > 50)
        {
            return false;
        }

        var known = (await db.NotDuplicates.Where(pair => mine.Contains(pair.BookId)).Select(pair => new { pair.BookId, pair.OtherId }).ToListAsync(cancellationToken))
            .Select(pair => (pair.BookId, pair.OtherId)).ToHashSet();
        for (var first = 0; first < mine.Count; first++)
        {
            for (var second = first + 1; second < mine.Count; second++)
            {
                if (known.Add((mine[first], mine[second])))
                {
                    db.NotDuplicates.Add(new NotDuplicate { OwnerId = db.ReaderId, BookId = mine[first], OtherId = mine[second] });
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
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
        var together = new HashSet<(int, int)>();
        void Note(IEnumerable<int> ids)
        {
            var list = ids.Order().ToList();
            for (var first = 0; first < list.Count; first++)
            {
                for (var second = first + 1; second < list.Count; second++)
                {
                    together.Add((list[first], list[second]));
                }
            }
        }

        foreach (var group in books.Where(book => !string.IsNullOrWhiteSpace(book.Isbn)).GroupBy(book => book.Isbn!).Where(group => group.Count() > 1))
        {
            seen.Add(Key(group.Select(book => book.Id)));
            Note(group.Select(book => book.Id));
            groups.Add(new DuplicateGroup("isbn", group.OrderBy(book => book.AddedAt).Select(book => shown[book.Id]).ToArray(), 100));
        }

        foreach (var group in books.GroupBy(book => (Simple(book.Title), Simple(book.Author))).Where(group => group.Key.Item1.Length > 0 && group.Count() > 1))
        {
            if (seen.Add(Key(group.Select(book => book.Id))))
            {
                Note(group.Select(book => book.Id));
                groups.Add(new DuplicateGroup("title", group.OrderBy(book => book.AddedAt).Select(book => shown[book.Id]).ToArray(), 90));
            }
        }

        // By the same author however written, titles alike: the same but for a subtitle or what is in brackets, or
        // sharing most of their words. Pairs found join into groups; a group is as sure as its least sure pair.
        var parent = new Dictionary<int, int>();
        int Find(int id) => parent.TryGetValue(id, out var up) && up != id ? parent[id] = Find(up) : id;
        var pairScores = new Dictionary<(int, int), int>();
        foreach (var author in books.Where(book => book.Author != BookImport.UnknownAuthor).GroupBy(book => Tidy.AuthorKey(book.Author)).Where(group => group.Key.Length > 0 && group.Count() is > 1 and <= MaxAuthorBooks))
        {
            var titles = author.Select(book => (book.Id, Base: BaseTitle(book.Title))).Where(item => item.Base.Words.Length > 0).ToList();
            for (var first = 0; first < titles.Count; first++)
            {
                for (var second = first + 1; second < titles.Count; second++)
                {
                    var (one, other) = (titles[first], titles[second]);
                    var pair = one.Id < other.Id ? (one.Id, other.Id) : (other.Id, one.Id);
                    if (together.Contains(pair) || Likeness(one.Base, other.Base) is not int score)
                    {
                        continue;
                    }

                    pairScores[pair] = score;
                    parent.TryAdd(pair.Item1, pair.Item1);
                    parent.TryAdd(pair.Item2, pair.Item2);
                    parent[Find(pair.Item1)] = Find(pair.Item2);
                }
            }
        }

        foreach (var component in parent.Keys.GroupBy(Find).Where(group => group.Count() > 1))
        {
            var ids = component.ToHashSet();
            var score = pairScores.Where(pair => ids.Contains(pair.Key.Item1)).Min(pair => pair.Value);
            if (seen.Add(Key(ids)))
            {
                groups.Add(new DuplicateGroup("similar", ids.Select(id => shown[id]).OrderBy(book => book.AddedAt).ToArray(), score));
            }
        }

        // Pairs the reader said are different come apart; a book left with no one to match leaves its group.
        var apart = (await db.NotDuplicates.AsNoTracking().Select(pair => new { pair.BookId, pair.OtherId }).ToListAsync(cancellationToken))
            .Select(pair => (pair.BookId, pair.OtherId)).ToHashSet();
        bool Apart(int one, int other) => apart.Contains(one < other ? (one, other) : (other, one));
        return groups
            .Select(group => group with { Books = group.Books.Where(book => group.Books.Any(other => other.Id != book.Id && !Apart(book.Id, other.Id))).ToArray() })
            .Where(group => group.Books.Length > 1)
            .OrderByDescending(group => group.Score)
            .ThenBy(group => group.Books[0].Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    // An author with more books than this (often a catch-all such as "Various") is not compared title by title.
    private const int MaxAuthorBooks = 800;

    private static readonly HashSet<string> Articles = ["the", "a", "an", "le", "la", "les", "l", "el", "los", "las", "der", "die", "das", "il", "lo"];
    private static readonly HashSet<string> EditionWords = ["edition", "unabridged", "abridged", "illustrated", "annotated", "novel", "book", "volume", "vol"];

    // A title's words without its subtitle, what is in brackets, a leading article, or words that only say which
    // edition it is.
    public static (string Joined, string[] Words) BaseTitle(string title) => TitleWords(title, subtitle: false);

    // As BaseTitle, but with its subtitle kept: "Star Wars: Thrawn" is not "Star Wars: Heir to the Empire".
    public static (string Joined, string[] Words) FullTitle(string title) => TitleWords(title, subtitle: true);

    private static (string Joined, string[] Words) TitleWords(string title, bool subtitle)
    {
        var main = BracketsPattern().Replace(title, " ");
        var cut = main.IndexOfAny([':', ';']);
        if (cut > 2 && !subtitle)
        {
            main = main[..cut];
        }

        var dash = main.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 2 && !subtitle)
        {
            main = main[..dash];
        }

        var words = Tidy.Plain(main).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(word => !EditionWords.Contains(word)).ToList();
        if (words.Count > 1 && Articles.Contains(words[0]))
        {
            words.RemoveAt(0);
        }

        return (string.Join(' ', words), [.. words]);
    }

    // How alike two titles are, out of 100, or null when not alike enough to show.
    public static int? Likeness((string Joined, string[] Words) one, (string Joined, string[] Words) other)
    {
        if (one.Joined == other.Joined)
        {
            return 85;
        }

        var (first, second) = (one.Words.ToHashSet(), other.Words.ToHashSet());
        if (first.Count < 3 || second.Count < 3)
        {
            return null;
        }

        var shared = first.Count(second.Contains);
        var likeness = (double)shared / (first.Count + second.Count - shared);
        return likeness >= 0.75 ? (int)Math.Round(likeness * 80) : null;
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
        // What moves is noted as it does, kept with the other entry in the trash, so restoring it takes it all back.
        var receipt = new MergeReceipt { KeptBookId = keepId };
        FillDetails(keep, other);
        foreach (var tag in other.Tags.Where(tag => keep.Tags.All(mine => mine.Id != tag.Id)).ToList())
        {
            keep.Tags.Add(tag);
            receipt.TagsAdded.Add(tag.Name);
        }

        // Files: one of a kind the book kept lacks comes across, with what belongs to it.
        var ebookMoves = keep.EbookStoredName is null && other.EbookStoredName is not null;
        var audioMoves = keep.AudioStoredName is null && other.AudioStoredName is not null;
        var coverMoves = keep.CoverImage is null && other.CoverImage is not null;
        if (ebookMoves)
        {
            (keep.EbookStoredName, keep.EbookFileName, keep.EbookChapter, keep.FileCover, keep.KoreaderDigest) = (other.EbookStoredName, other.EbookFileName, other.EbookChapter, other.FileCover, other.KoreaderDigest);
            receipt.Ebook = other.EbookStoredName;
            Unmerging.Moved(receipt, nameof(ShelfDb.OcrScans), await db.OcrScans.Where(scan => scan.BookId == otherId).Select(scan => scan.Id).ToListAsync(cancellationToken));
            Unmerging.Moved(receipt, nameof(ShelfDb.BookTexts), await db.BookTexts.Where(text => text.BookId == otherId).Select(text => text.Id).ToListAsync(cancellationToken));
            await db.OcrScans.Where(scan => scan.BookId == otherId).ExecuteUpdateAsync(set => set.SetProperty(scan => scan.BookId, keepId), cancellationToken);
            await db.BookTexts.Where(text => text.BookId == otherId).ExecuteUpdateAsync(set => set.SetProperty(text => text.BookId, keepId), cancellationToken);
        }

        // Other e-book formats: the merged-away book's, and its main e-book when the kept one's is another format,
        // come across where the kept book has none of that format.
        var keepFormats = (await db.EbookFormats.Where(format => format.BookId == keepId).Select(format => format.StoredName).ToListAsync(cancellationToken)).Select(EbookStore.FormatOf).ToHashSet();
        if (keep.EbookStoredName is { } keptMain)
        {
            keepFormats.Add(EbookStore.FormatOf(keptMain));
        }

        var movingFormats = (await db.EbookFormats.Where(format => format.BookId == otherId).ToListAsync(cancellationToken))
            .Where(format => keepFormats.Add(EbookStore.FormatOf(format.StoredName))).Select(format => format.Id).ToList();
        Unmerging.Moved(receipt, nameof(ShelfDb.EbookFormats), movingFormats);
        await db.EbookFormats.Where(format => movingFormats.Contains(format.Id)).ExecuteUpdateAsync(set => set.SetProperty(format => format.BookId, keepId), cancellationToken);
        var mainAsFormat = !ebookMoves && other.EbookStoredName is { } otherMain && keepFormats.Add(EbookStore.FormatOf(otherMain));
        if (mainAsFormat)
        {
            db.EbookFormats.Add(new EbookFormat { BookId = keepId, StoredName = other.EbookStoredName!, FileName = other.EbookFileName ?? other.EbookStoredName!, AddedAt = DateTimeOffset.UtcNow });
            receipt.EbookAsFormat = other.EbookStoredName;
        }

        if (audioMoves)
        {
            (keep.AudioStoredName, keep.AudioFileName, keep.AudioTrack, keep.AudioSeconds) = (other.AudioStoredName, other.AudioFileName, other.AudioTrack, other.AudioSeconds);
            (keep.AudioChapterNames, keep.Asin) = (other.AudioChapterNames, keep.Asin ?? other.Asin);
            receipt.Audio = other.AudioStoredName;
        }

        if (coverMoves)
        {
            keep.CoverImage = other.CoverImage;
            receipt.Cover = other.CoverImage;
        }

        // Everything written about the book, by the owner and by anyone it was lent to.
        var keptQuotes = (await db.Quotes.Where(quote => quote.BookId == keepId).Select(quote => quote.Text).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        Unmerging.Moved(receipt, nameof(ShelfDb.Quotes), (await db.Quotes.Where(quote => quote.BookId == otherId).Select(quote => new { quote.Id, quote.Text }).ToListAsync(cancellationToken))
            .Where(quote => !keptQuotes.Contains(quote.Text)).Select(quote => quote.Id));
        Unmerging.Moved(receipt, nameof(ShelfDb.Highlights), await db.Highlights.IgnoreQueryFilters().Where(mark => mark.BookId == otherId).Select(mark => mark.Id).ToListAsync(cancellationToken));
        Unmerging.Moved(receipt, nameof(ShelfDb.Sessions), await db.Sessions.IgnoreQueryFilters().Where(session => session.BookId == otherId).Select(session => session.Id).ToListAsync(cancellationToken));
        await db.Quotes.Where(quote => quote.BookId == otherId && !keptQuotes.Contains(quote.Text)).ExecuteUpdateAsync(set => set.SetProperty(quote => quote.BookId, keepId), cancellationToken);
        await db.Highlights.IgnoreQueryFilters().Where(mark => mark.BookId == otherId).ExecuteUpdateAsync(set => set.SetProperty(mark => mark.BookId, keepId), cancellationToken);
        await db.Sessions.IgnoreQueryFilters().Where(session => session.BookId == otherId).ExecuteUpdateAsync(set => set.SetProperty(session => session.BookId, keepId), cancellationToken);

        // The reader's own fields: a value the kept book has no value for comes across.
        var keptFields = await db.CustomValues.Where(value => value.BookId == keepId).Select(value => value.FieldId).ToListAsync(cancellationToken);
        var movedValues = await db.CustomValues.Where(value => value.BookId == otherId && !keptFields.Contains(value.FieldId)).Select(value => value.Id).ToListAsync(cancellationToken);
        Unmerging.Moved(receipt, nameof(ShelfDb.CustomValues), movedValues);
        await db.CustomValues.Where(value => movedValues.Contains(value.Id)).ExecuteUpdateAsync(set => set.SetProperty(value => value.BookId, keepId), cancellationToken);
        if (audioMoves || keep.AudioStoredName is null)
        {
            Unmerging.Moved(receipt, nameof(ShelfDb.AudioBookmarks), await db.AudioBookmarks.IgnoreQueryFilters().Where(mark => mark.BookId == otherId).Select(mark => mark.Id).ToListAsync(cancellationToken));
            await db.AudioBookmarks.IgnoreQueryFilters().Where(mark => mark.BookId == otherId).ExecuteUpdateAsync(set => set.SetProperty(mark => mark.BookId, keepId), cancellationToken);
        }

        if (ebookMoves)
        {
            var keptDevices = await db.KosyncPlaces.Where(place => place.BookId == keepId).Select(place => place.ReaderId).ToListAsync(cancellationToken);
            Unmerging.Moved(receipt, nameof(ShelfDb.KosyncPlaces), await db.KosyncPlaces.Where(place => place.BookId == otherId && !keptDevices.Contains(place.ReaderId)).Select(place => place.Id).ToListAsync(cancellationToken));
            await db.KosyncPlaces.Where(place => place.BookId == otherId && !keptDevices.Contains(place.ReaderId)).ExecuteUpdateAsync(set => set.SetProperty(place => place.BookId, keepId), cancellationToken);
        }

        // Earlier reads, pictures of the copy, and journal entries come across; links to the other book go to this
        // one, except those that would join it to itself.
        Unmerging.Moved(receipt, nameof(ShelfDb.PastReads), await db.PastReads.Where(read => read.BookId == otherId).Select(read => read.Id).ToListAsync(cancellationToken));
        Unmerging.Moved(receipt, nameof(ShelfDb.CopyPhotos), await db.CopyPhotos.Where(photo => photo.BookId == otherId).Select(photo => photo.Id).ToListAsync(cancellationToken));
        Unmerging.Moved(receipt, nameof(ShelfDb.JournalEntries), await db.JournalEntries.Where(entry => entry.BookId == otherId).Select(entry => entry.Id).ToListAsync(cancellationToken));
        receipt.LinksRemoved.AddRange((await db.BookLinks.Where(link => (link.FromBookId == otherId && link.ToBookId == keepId) || (link.FromBookId == keepId && link.ToBookId == otherId)).ToListAsync(cancellationToken))
            .Select(link => new MergeLink(link.FromBookId == otherId, link.Kind, link.Note)));
        receipt.LinksFrom.AddRange(await db.BookLinks.Where(link => link.FromBookId == otherId && link.ToBookId != keepId).Select(link => link.Id).ToListAsync(cancellationToken));
        receipt.LinksTo.AddRange(await db.BookLinks.Where(link => link.ToBookId == otherId && link.FromBookId != keepId).Select(link => link.Id).ToListAsync(cancellationToken));
        await db.PastReads.Where(read => read.BookId == otherId).ExecuteUpdateAsync(set => set.SetProperty(read => read.BookId, keepId), cancellationToken);
        await db.CopyPhotos.Where(photo => photo.BookId == otherId).ExecuteUpdateAsync(set => set.SetProperty(photo => photo.BookId, keepId), cancellationToken);
        await db.JournalEntries.Where(entry => entry.BookId == otherId).ExecuteUpdateAsync(set => set.SetProperty(entry => entry.BookId, keepId), cancellationToken);
        await db.BookLinks.Where(link => (link.FromBookId == otherId && link.ToBookId == keepId) || (link.FromBookId == keepId && link.ToBookId == otherId)).ExecuteDeleteAsync(cancellationToken);
        await db.BookLinks.Where(link => link.FromBookId == otherId).ExecuteUpdateAsync(set => set.SetProperty(link => link.FromBookId, keepId), cancellationToken);
        await db.BookLinks.Where(link => link.ToBookId == otherId).ExecuteUpdateAsync(set => set.SetProperty(link => link.ToBookId, keepId), cancellationToken);

        var keptIn = await db.CollectionBooks.IgnoreQueryFilters().Where(entry => entry.BookId == keepId).Select(entry => entry.CollectionId).ToListAsync(cancellationToken);
        receipt.Collections.AddRange(await db.CollectionBooks.IgnoreQueryFilters().Where(entry => entry.BookId == otherId && !keptIn.Contains(entry.CollectionId)).Select(entry => entry.CollectionId).ToListAsync(cancellationToken));
        await db.CollectionBooks.IgnoreQueryFilters().Where(entry => entry.BookId == otherId && !keptIn.Contains(entry.CollectionId))
            .ExecuteUpdateAsync(set => set.SetProperty(entry => entry.BookId, keepId), cancellationToken);
        await MoveDaysAsync(db, keepId, otherId, receipt, cancellationToken);

        // The other entry goes to the trash with what is left of it and the files it kept, should the merge have been
        // a mistake; a file that came across is the kept book's now and no longer the other's.
        other.EbookStoredName = ebookMoves || mainAsFormat ? null : other.EbookStoredName;
        other.AudioStoredName = audioMoves ? null : other.AudioStoredName;
        other.CoverImage = coverMoves ? null : other.CoverImage;
        Unmerging.NoteKept(db, keep, receipt);
        await db.SaveChangesAsync(cancellationToken);
        await Trash.DeleteBooksAsync(db, ebooks, audio, covers, [otherId], cancellationToken: cancellationToken);
        if (await db.TrashItems.Where(item => item.Kind == TrashKind.Book && item.BookId == otherId).OrderByDescending(item => item.Id).FirstOrDefaultAsync(cancellationToken) is { } trashed)
        {
            trashed.MergeReceipt = JsonSerializer.Serialize(receipt, History.JsonOptions);
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
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
    private static async Task MoveDaysAsync(ShelfDb db, int keepId, int otherId, MergeReceipt receipt, CancellationToken cancellationToken)
    {
        var listened = await db.ListeningDays.IgnoreQueryFilters().Where(day => day.BookId == keepId || day.BookId == otherId).ToListAsync(cancellationToken);
        foreach (var row in listened.Where(day => day.BookId == otherId).ToList())
        {
            if (listened.FirstOrDefault(day => day.BookId == keepId && day.ReaderId == row.ReaderId && day.Day == row.Day) is { } into)
            {
                receipt.ListeningAdded.Add(new MergeDay(into.Id, row.ReaderId, row.Day, row.Seconds));
                into.Seconds += row.Seconds;
                db.ListeningDays.Remove(row);
            }
            else
            {
                Unmerging.Moved(receipt, nameof(ShelfDb.ListeningDays), [row.Id]);
                row.BookId = keepId;
            }
        }

        var read = await db.ReadingDays.IgnoreQueryFilters().Where(day => day.BookId == keepId || day.BookId == otherId).ToListAsync(cancellationToken);
        foreach (var row in read.Where(day => day.BookId == otherId).ToList())
        {
            if (read.FirstOrDefault(day => day.BookId == keepId && day.ReaderId == row.ReaderId && day.Day == row.Day) is { } into)
            {
                receipt.ReadingAdded.Add(new MergeDay(into.Id, row.ReaderId, row.Day, row.Seconds, row.DeviceSeconds, into.DeviceSeconds));
                into.Seconds += row.Seconds;
                into.DeviceSeconds = Math.Max(into.DeviceSeconds, row.DeviceSeconds);
                db.ReadingDays.Remove(row);
            }
            else
            {
                Unmerging.Moved(receipt, nameof(ShelfDb.ReadingDays), [row.Id]);
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

    [GeneratedRegex(@"[\(\[][^\)\]]*[\)\]]")]
    private static partial Regex BracketsPattern();
}

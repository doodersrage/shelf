using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Books;

public enum NextReason
{
    // The next book in a series the reader is reading or has finished a book of.
    NextInSeries,

    // By an author whose books the reader loved or rated highly.
    Author,

    // Sharing subjects with books the reader loved or rated highly.
    Subjects,

    // Put in the reader's queue.
    Queued,
}

// A book to read next, and why: the book it follows or resembles, and for subjects, the ones in common.
public sealed record NextRead(int? BookId, string Title, string Author, string? Cover, NextReason Reason, string? Because, string[] Subjects, bool HasEbook, bool HasAudio, int? OwnerId = null, string? Owner = null);

public sealed record NextReads(NextRead[] Series, SeriesAlertResponse[] NewInSeries, NextRead[] Shelf, NextRead[] Borrowable);

// Suggestions from what is already here: the reader's own want-to-read books, ranked by what they loved, the next
// books in their series, and books on open shelves by authors and on subjects they love. Nothing is sent anywhere.
public static class NextReadsRules
{
    public const int MostShown = 12;

    public static void Map(RouteGroupBuilder books) =>
        books.MapGet("/next", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await SuggestAsync(db, cancellationToken)));

    public static async Task<NextReads> SuggestAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        // Only what choosing and showing a suggestion needs; a big shelf's chapter lists and notes are slow to read.
        var books = await db.Books.AsNoTracking()
            .Select(book => new Book
            {
                Id = book.Id,
                Title = book.Title,
                Author = book.Author,
                Series = book.Series,
                SeriesNumber = book.SeriesNumber,
                Status = book.Status,
                Loved = book.Loved,
                Rating = book.Rating,
                Queued = book.Queued,
                AddedAt = book.AddedAt,
                CoverUrl = book.CoverUrl,
                FileCover = book.FileCover,
                CoverImage = book.CoverImage,
                EbookStoredName = book.EbookStoredName,
                AudioStoredName = book.AudioStoredName,
                Tags = book.Tags.ToList(),
            })
            .ToListAsync(cancellationToken);
        var loved = books.Where(book => book.Loved || book.Rating >= 4).ToList();
        var lovedAuthors = loved.GroupBy(book => Simple(book.Author)).Where(group => group.Key.Length > 0).ToDictionary(group => group.Key, group => group.OrderByDescending(book => book.Rating ?? 0).First());
        var lovedTags = loved.SelectMany(book => book.Tags.Select(tag => tag.Name)).GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        // The next unread book in each series a book of which is being read or was finished.
        var series = books
            .Where(book => !string.IsNullOrWhiteSpace(book.Series))
            .GroupBy(book => (Simple(book.Series), Simple(book.Author)))
            .Select(group =>
            {
                var read = group.Where(book => book.Status is BookStatus.Reading or BookStatus.Finished).ToList();
                if (read.Count == 0)
                {
                    return null;
                }

                var furthest = read.Max(book => book.SeriesNumber ?? 0);
                var next = group.Where(book => book.Status == BookStatus.Want && (book.SeriesNumber ?? int.MaxValue) > furthest)
                    .OrderBy(book => book.SeriesNumber ?? int.MaxValue).ThenBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase).FirstOrDefault();
                var before = read.OrderByDescending(book => book.SeriesNumber ?? 0).First();
                return next is null ? null : Suggest(next, NextReason.NextInSeries, before.Title, []);
            })
            .OfType<NextRead>()
            .Take(MostShown)
            .ToArray();
        // Later books in a series under way wait for the next one, so they are not offered out of order.
        var followed = books.Where(book => series.Any(next => next.BookId == book.Id)).Select(book => (Simple(book.Series), Simple(book.Author))).ToHashSet();

        // The want-to-read list, best first: queued, then by a loved author, then by subjects in common.
        var shelf = books
            .Where(book => book.Status == BookStatus.Want && (string.IsNullOrWhiteSpace(book.Series) || !followed.Contains((Simple(book.Series), Simple(book.Author)))))
            .Select(book =>
            {
                var common = book.Tags.Select(tag => tag.Name).Where(lovedTags.ContainsKey).OrderByDescending(name => lovedTags[name]).ToArray();
                var byAuthor = lovedAuthors.GetValueOrDefault(Simple(book.Author));
                var score = (book.Queued ? 100 : 0) + (byAuthor is not null ? 50 : 0) + common.Sum(name => Math.Min(lovedTags[name], 5) * 5) + (book.Loved ? 10 : 0);
                return (Score: score, Book: book, Common: common, ByAuthor: byAuthor);
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Book.AddedAt)
            .Take(MostShown)
            // Only those shown are given their reason, which looks through the loved books for one in common.
            .Select(item =>
            {
                var reason = item.Book.Queued ? NextReason.Queued : item.ByAuthor is not null ? NextReason.Author : NextReason.Subjects;
                var because = reason == NextReason.Author ? item.ByAuthor!.Title : reason == NextReason.Subjects ? loved.FirstOrDefault(favourite => favourite.Tags.Any(tag => item.Common.Contains(tag.Name, StringComparer.OrdinalIgnoreCase)))?.Title : null;
                return Suggest(item.Book, reason, because, item.Common.Take(3).ToArray());
            })
            .ToArray();

        return new NextReads(series, await SeriesWatch.ListAsync(db, cancellationToken), shelf, await BorrowableAsync(db, books, lovedAuthors, cancellationToken));
    }

    // Books on other readers' open shelves, not on this one, by loved authors. Only what an open shelf shows anyone
    // is weighed (title, author, cover), never the owner's own tags, stars, or statuses.
    private static async Task<NextRead[]> BorrowableAsync(ShelfDb db, List<Book> mine, Dictionary<string, Book> lovedAuthors, CancellationToken cancellationToken)
    {
        var me = db.ReaderId;
        if (me == 0 || lovedAuthors.Count == 0 || await Family.IsRestrictedAsync(db, me, cancellationToken))
        {
            return [];
        }

        var titles = mine.Select(book => Simple(book.Title)).ToHashSet(StringComparer.Ordinal);
        var theirs = await db.Books.IgnoreQueryFilters().AsNoTracking()
            .Where(book => book.OwnerId != me && book.Owner != null && book.Owner.ShelfOpen && !book.Owner.Restricted)
            .Select(book => new { book.Id, book.Title, book.Author, book.CoverUrl, book.OwnerId, Owner = book.Owner!.Name })
            .ToListAsync(cancellationToken);
        return [.. theirs
            .Where(item => !titles.Contains(Simple(item.Title)) && lovedAuthors.ContainsKey(Simple(item.Author)))
            .Take(MostShown)
            .Select(item => new NextRead(item.Id, item.Title, item.Author, item.CoverUrl, NextReason.Author, lovedAuthors[Simple(item.Author)].Title, [], false, false)
                with { OwnerId = item.OwnerId, Owner = item.Owner, Cover = item.CoverUrl })];
    }

    private static NextRead Suggest(Book book, NextReason reason, string? because, string[] subjects) =>
        new(book.Id, book.Title, book.Author, book.CoverShown, reason, because, subjects, book.EbookStoredName is not null, book.AudioStoredName is not null);

    private static string Simple(string? text) => new((text ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}

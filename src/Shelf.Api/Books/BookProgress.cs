using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

// How far a reader is through a book, by its recording when that is what they are on, else by its pages.
public static class BookProgress
{
    // How far into its recording a book is, and how long the recording runs, when every track's length is known.
    public static (double Heard, double Length)? Listened(Book book, AudioStore audio)
    {
        if (string.IsNullOrWhiteSpace(book.AudioStoredName))
        {
            return null;
        }

        var lengths = audio.Lengths(book.AudioStoredName);
        if (lengths.Count == 0 || lengths.Any(length => length is null))
        {
            return null;
        }

        var track = Math.Clamp(book.AudioTrack ?? 0, 0, lengths.Count - 1);
        var heard = lengths.Take(track).Sum(length => length!.Value) + (book.AudioSeconds ?? 0);
        var total = lengths.Sum(length => length!.Value);
        return total > 0 ? (Math.Min(heard, total), total) : null;
    }

    // Whether the recording is the measure: an audiobook, or a book with a recording and no page count.
    public static bool ByRecording(Book book) =>
        !string.IsNullOrWhiteSpace(book.AudioStoredName) && (book.Format == BookFormat.Audiobook || book.Pages is null);

    // From nothing to one; null when there is nothing to measure by.
    public static double? Fraction(Book book, AudioStore audio)
    {
        if (ByRecording(book) && Listened(book, audio) is var (heard, length))
        {
            return heard / length;
        }

        // The page reached, or the place in the e-book (Shelf's reader, KOReader, and Kobo move that, not the page),
        // whichever is further: by pages alone, a book read far in the reader showed nothing done.
        double? byPage = book.Pages is int pages && pages > 0 ? Math.Clamp((book.CurrentPage ?? 0) / (double)pages, 0, 1) : null;
        double? byPlace = book.EbookProgress is double place and > 0 ? Math.Clamp(place, 0, 1) : null;
        return byPage is null && byPlace is null ? null : Math.Max(byPage ?? 0, byPlace ?? 0);
    }

    // The open books, the one last read or listened to first; one only started, with nothing logged, after those.
    public static async Task<List<Book>> LatestFirstAsync(ShelfDb db, IEnumerable<Book> books, CancellationToken cancellationToken = default)
    {
        var list = books.ToList();
        // Every book's last day, not just these: a list of thousands of ids in a query is far slower than the lot.
        var logged = await db.Sessions.AsNoTracking()
            .GroupBy(session => session.BookId).Select(group => new { group.Key, Last = group.Max(session => session.Date) }).ToDictionaryAsync(item => item.Key, item => item.Last, cancellationToken);
        var heard = await db.ListeningDays.AsNoTracking().Where(day => day.ReaderId == db.ReaderId)
            .GroupBy(day => day.BookId).Select(group => new { group.Key, Last = group.Max(day => day.Day) }).ToDictionaryAsync(item => item.Key, item => item.Last, cancellationToken);
        DateOnly Touched(Book book) => new[] { logged.GetValueOrDefault(book.Id), heard.GetValueOrDefault(book.Id) }.Max();
        return [.. list.OrderByDescending(Touched).ThenByDescending(book => book.StartedOn ?? DateOnly.MinValue).ThenByDescending(book => book.AddedAt)];
    }

    public static string Span(double seconds)
    {
        var minutes = (int)Math.Round(Math.Max(0, seconds) / 60);
        return minutes >= 60 ? Localization.Words.T("{0} h {1} min", minutes / 60, minutes % 60) : Localization.Words.T("{0} min", minutes);
    }
}

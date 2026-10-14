using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public enum NoteKind
{
    Note,
    Review,
    Quote,
    Highlight,
    Session,
    Journal,
    Research,
}

// A reader's own words that say what was searched for, shown in their context.
public sealed record NoteHit(NoteKind Kind, int? BookId, string? Title, string? Author, string Before, string Match, string After, string Link, DateTimeOffset? At);

public sealed record NoteFilter(NoteKind? Kind = null, BookStatus? Status = null, string? Author = null, BookFormat? Format = null);

// Search through what the reader wrote: notes and reviews on books, quotes and highlights, the reading log, the
// journal, and the reference desk. Only the reader's own; nothing a borrower marked in their books.
public static class NoteSearch
{
    public const int MaxHits = 200;
    private const int Around = 70;

    public static void Map(RouteGroupBuilder books) =>
        books.MapGet("/notes/search", async (string? q, NoteKind? kind, BookStatus? status, string? author, BookFormat? format, ShelfDb db, CancellationToken cancellationToken) =>
            TypedResults.Ok(await FindAsync(db, q, new NoteFilter(kind, status, author, format), cancellationToken))).WithTags("Your reading");

    public static string KindLabel(NoteKind kind) => kind switch
    {
        NoteKind.Note => T("Notes"),
        NoteKind.Review => T("Reviews"),
        NoteKind.Quote => T("Quotes"),
        NoteKind.Highlight => T("Highlights"),
        NoteKind.Session => T("Reading log"),
        NoteKind.Journal => T("Journal"),
        _ => T("Reference desk"),
    };

    public static async Task<NoteHit[]> FindAsync(ShelfDb db, string? q, NoteFilter? filter = null, CancellationToken cancellationToken = default)
    {
        var term = q?.Trim();
        if (string.IsNullOrEmpty(term) || term.Length < Search.MinLength)
        {
            return [];
        }

        filter ??= new NoteFilter();
        var lower = term.ToLowerInvariant();
        var books = db.Books.AsNoTracking();
        if (filter.Status is BookStatus status)
        {
            books = books.Where(book => book.Status == status);
        }

        if (filter.Format is BookFormat format)
        {
            books = books.Where(book => book.Format == format);
        }

        if (!string.IsNullOrWhiteSpace(filter.Author))
        {
            var author = filter.Author.Trim().ToLower();
            books = books.Where(book => book.Author.ToLower().Contains(author));
        }

        var bookFiltered = filter.Status is not null || filter.Format is not null || !string.IsNullOrWhiteSpace(filter.Author);
        bool Wants(NoteKind kind) => filter.Kind is null || filter.Kind == kind;
        var hits = new List<NoteHit>();

        if (Wants(NoteKind.Note) || Wants(NoteKind.Review))
        {
            var found = await books.Where(book => (book.Notes != null && book.Notes.ToLower().Contains(lower)) || (book.Review != null && book.Review.ToLower().Contains(lower)))
                .Select(book => new { book.Id, book.Title, book.Author, book.Notes, book.Review, book.AddedAt }).Take(MaxHits).ToListAsync(cancellationToken);
            foreach (var book in found)
            {
                if (Wants(NoteKind.Note) && Cut(book.Notes, term) is var (before, match, after))
                {
                    hits.Add(new NoteHit(NoteKind.Note, book.Id, book.Title, book.Author, before, match, after, $"/library/{book.Id}#details", null));
                }

                if (Wants(NoteKind.Review) && Cut(book.Review, term) is var (b, m, a))
                {
                    hits.Add(new NoteHit(NoteKind.Review, book.Id, book.Title, book.Author, b, m, a, $"/library/{book.Id}#details", null));
                }
            }
        }

        if (Wants(NoteKind.Quote))
        {
            var found = await db.Quotes.AsNoTracking().Where(quote => books.Any(book => book.Id == quote.BookId) && quote.Text.ToLower().Contains(lower))
                .OrderByDescending(quote => quote.Id).Take(MaxHits)
                .Select(quote => new { quote.BookId, quote.Book!.Title, quote.Book.Author, quote.Text, quote.NotedAt }).ToListAsync(cancellationToken);
            hits.AddRange(found.Select(quote => Cut(quote.Text, term) is var (before, match, after)
                ? new NoteHit(NoteKind.Quote, quote.BookId, quote.Title, quote.Author, before, match, after, $"/library/{quote.BookId}#quotes", quote.NotedAt) : null).OfType<NoteHit>());
        }

        if (Wants(NoteKind.Highlight))
        {
            var found = await db.Highlights.AsNoTracking().Where(mark => books.Any(book => book.Id == mark.BookId) && (mark.Text.ToLower().Contains(lower) || (mark.Note != null && mark.Note.ToLower().Contains(lower))))
                .OrderByDescending(mark => mark.Id).Take(MaxHits)
                .Select(mark => new { mark.BookId, mark.Book!.Title, mark.Book.Author, mark.Text, mark.Note, mark.NotedAt, mark.ChapterIndex }).ToListAsync(cancellationToken);
            hits.AddRange(found.Select(mark => (Cut(mark.Text, term) ?? Cut(mark.Note, term)) is var (before, match, after)
                ? new NoteHit(NoteKind.Highlight, mark.BookId, mark.Title, mark.Author, before, match, after, $"/library/{mark.BookId}/read?chapter={mark.ChapterIndex}", mark.NotedAt) : null).OfType<NoteHit>());
        }

        if (Wants(NoteKind.Session))
        {
            var found = await db.Sessions.AsNoTracking().Where(session => books.Any(book => book.Id == session.BookId) && session.Note != null && session.Note.ToLower().Contains(lower))
                .OrderByDescending(session => session.Date).Take(MaxHits)
                .Select(session => new { session.BookId, session.Book!.Title, session.Book.Author, session.Note, session.Date }).ToListAsync(cancellationToken);
            hits.AddRange(found.Select(session => Cut(session.Note, term) is var (before, match, after)
                ? new NoteHit(NoteKind.Session, session.BookId, session.Title, session.Author, before, match, after, $"/library/{session.BookId}#reading",
                    new DateTimeOffset(session.Date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)) : null).OfType<NoteHit>());
        }

        if (Wants(NoteKind.Journal) && !bookFiltered)
        {
            var found = await db.JournalEntries.AsNoTracking().Where(entry => entry.Text.ToLower().Contains(lower))
                .OrderByDescending(entry => entry.Date).Take(MaxHits).ToListAsync(cancellationToken);
            var titles = await Titles(db, found.Select(entry => entry.BookId), cancellationToken);
            hits.AddRange(found.Select(entry => Cut(entry.Text, term) is var (before, match, after)
                ? new NoteHit(NoteKind.Journal, entry.BookId, entry.BookId is int id ? titles.GetValueOrDefault(id).Title : null, entry.BookId is int other ? titles.GetValueOrDefault(other).Author : null,
                    before, match, after, "/journal", new DateTimeOffset(entry.Date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)) : null).OfType<NoteHit>());
        }

        if (Wants(NoteKind.Research) && !bookFiltered)
        {
            var found = await db.ResearchNotes.AsNoTracking().Where(note => (note.Text != null && note.Text.ToLower().Contains(lower)) || (note.Quote != null && note.Quote.ToLower().Contains(lower)))
                .OrderByDescending(note => note.Id).Take(MaxHits)
                .Select(note => new { note.TopicId, note.BookId, note.Text, note.Quote, note.CreatedAt }).ToListAsync(cancellationToken);
            var titles = await Titles(db, found.Select(note => note.BookId), cancellationToken);
            hits.AddRange(found.Select(note => (Cut(note.Text, term) ?? Cut(note.Quote, term)) is var (before, match, after)
                ? new NoteHit(NoteKind.Research, note.BookId, note.BookId is int id ? titles.GetValueOrDefault(id).Title : null, note.BookId is int other ? titles.GetValueOrDefault(other).Author : null,
                    before, match, after, $"/research/{note.TopicId}", note.CreatedAt) : null).OfType<NoteHit>());
        }

        return [.. hits.OrderByDescending(hit => hit.At ?? DateTimeOffset.MinValue).Take(MaxHits)];
    }

    private static async Task<Dictionary<int, (string Title, string Author)>> Titles(ShelfDb db, IEnumerable<int?> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.OfType<int>().Distinct().ToList();
        return wanted.Count == 0 ? [] : await db.Books.AsNoTracking().Where(book => wanted.Contains(book.Id))
            .ToDictionaryAsync(book => book.Id, book => (book.Title, book.Author), cancellationToken);
    }

    // The words around the first place the text says it, case and accents aside.
    public static (string Before, string Match, string After)? Cut(string? text, string term)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var at = System.Globalization.CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, term, System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace, out var length);
        if (at < 0)
        {
            return null;
        }

        var start = Math.Max(0, at - Around);
        var end = Math.Min(text.Length, at + length + Around);
        return ((start > 0 ? "…" : "") + text[start..at], text.Substring(at, length), text[(at + length)..end] + (end < text.Length ? "…" : ""));
    }
}

using Microsoft.EntityFrameworkCore;
using Shelf.Api.Components.Layout;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// One place quick jump can go: what kind of place it is, its name, a word more about it, and its address.
public sealed record JumpHit(string Kind, string Title, string? Detail, string Href);

// Quick jump (Ctrl K, or "/"): the pages of Shelf by name or by what they're for, and the reader's own books,
// authors, series, places, and collections, by a few letters of their names.
public static class Jump
{
    public const int MaxQuery = 100;
    private const int Pages = 8;
    private const int MostBooks = 6;
    private const int Others = 3;

    public static async Task<JumpHit[]> FindAsync(ShelfDb db, string? query, CancellationToken cancellationToken = default)
    {
        var admin = await ReaderRules.IsAdminAsync(db, db.ReaderId);
        var child = await Family.IsRestrictedAsync(db);
        var fetching = Fetcher.Ready(await FetchRules.SettingsAsync(db)) && !child;
        var pages = NavMap.Top()
            .Concat(NavMap.Groups().SelectMany(group => group.Links))
            .Concat(NavMap.Elsewhere())
            .Where(link => NavMap.Allowed(link, admin, child, fetching))
            .DistinctBy(link => link.Href)
            .ToList();
        var term = (query ?? "").Trim();
        term = term.Length > MaxQuery ? term[..MaxQuery] : term;
        var page = T("Page");
        if (term.Length == 0)
        {
            return [.. pages.Take(Pages).Select(link => new JumpHit(page, link.Label, null, link.Href))];
        }

        var plain = Tidy.Plain(term);
        var hits = pages
            .Select(link => (Link: link, Rank: Rank(Tidy.Plain(link.Label), plain) ?? (Tidy.Plain(link.Also ?? "").Contains(plain, StringComparison.Ordinal) ? 4 : (int?)null)))
            .Where(item => item.Rank is not null)
            .OrderBy(item => item.Rank).ThenBy(item => item.Link.Label.Length)
            .Take(Pages)
            .Select(item => new JumpHit(page, item.Link.Label, null, item.Link.Href))
            .ToList();
        if (term.Length < 2)
        {
            return [.. hits];
        }

        var escaped = term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        var (starting, anywhere) = (escaped + "%", "%" + escaped + "%");

        // Titles that begin with what was typed first, as at 70,000 books a common word is in thousands of them.
        var first = await Books(db, db.Books.AsNoTracking(), starting, authors: false).Take(MostBooks).ToListAsync(cancellationToken);
        var more = await Books(db, db.Books.AsNoTracking(), anywhere, authors: true).Take(200).ToListAsync(cancellationToken);
        hits.AddRange(first.Concat(more.OrderBy(book => Rank(Tidy.Plain(book.Title), plain) ?? 9).ThenBy(book => book.Title.Length))
            .DistinctBy(book => book.Id)
            .Take(MostBooks)
            .Select(book => new JumpHit(T("Book"), book.Title, book.Author, $"/library/{book.Id}")));

        hits.AddRange(more.Select(book => book.Author).Where(author => Tidy.Plain(author).Contains(plain, StringComparison.Ordinal))
            .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(author => author.Length).Take(Others)
            .Select(author => new JumpHit(T("Author"), author, null, $"/library?author={Uri.EscapeDataString(author)}")));
        hits.AddRange(more.Select(book => book.Series).OfType<string>().Where(series => Tidy.Plain(series).Contains(plain, StringComparison.Ordinal))
            .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(series => series.Length).Take(Others)
            .Select(series => new JumpHit(T("Series"), series, null, $"/library?series={Uri.EscapeDataString(series)}")));

        var places = db.IsPostgres
            ? db.StoragePlaces.AsNoTracking().Where(place => EF.Functions.ILike(place.Name, anywhere, "\\"))
            : db.StoragePlaces.AsNoTracking().Where(place => EF.Functions.Like(place.Name, anywhere, "\\"));
        hits.AddRange((await places.OrderBy(place => place.Name).Take(Others).Select(place => new { place.Id, place.Name }).ToListAsync(cancellationToken))
            .Select(place => new JumpHit(T("Place"), place.Name, null, $"/places/{place.Id}")));
        var collections = db.IsPostgres
            ? db.Collections.AsNoTracking().Where(collection => EF.Functions.ILike(collection.Name, anywhere, "\\"))
            : db.Collections.AsNoTracking().Where(collection => EF.Functions.Like(collection.Name, anywhere, "\\"));
        hits.AddRange((await collections.OrderBy(collection => collection.Name).Take(Others).Select(collection => new { collection.Id, collection.Name }).ToListAsync(cancellationToken))
            .Select(collection => new JumpHit(T("Collection"), collection.Name, null, $"/collections/{collection.Id}")));
        return [.. hits];
    }

    private sealed record Found(int Id, string Title, string Author, string? Series);

    // Books whose title (and, if asked, author or series) is like the pattern. SQLite's LIKE ignores case already.
    private static IQueryable<Found> Books(ShelfDb db, IQueryable<Book> books, string pattern, bool authors) =>
        (db.IsPostgres
            ? books.Where(book => EF.Functions.ILike(book.Title, pattern, "\\")
                || (authors && (EF.Functions.ILike(book.Author, pattern, "\\") || (book.Series != null && EF.Functions.ILike(book.Series, pattern, "\\")))))
            : books.Where(book => EF.Functions.Like(book.Title, pattern, "\\")
                || (authors && (EF.Functions.Like(book.Author, pattern, "\\") || (book.Series != null && EF.Functions.Like(book.Series, pattern, "\\"))))))
        .Select(book => new Found(book.Id, book.Title, book.Author, book.Series));

    // How well a name fits what was typed: from its start, from the start of a word in it, anywhere in it, or not.
    private static int? Rank(string name, string typed) =>
        name.StartsWith(typed, StringComparison.Ordinal) ? 1
        : name.Contains(" " + typed, StringComparison.Ordinal) ? 2
        : name.Contains(typed, StringComparison.Ordinal) ? 3
        : null;

    public static void Map(RouteGroupBuilder books) =>
        books.MapGet("/jump", async (string? q, ShelfDb db, CancellationToken cancellationToken) =>
            TypedResults.Ok(await FindAsync(db, q, cancellationToken))).WithTags("Books");
}

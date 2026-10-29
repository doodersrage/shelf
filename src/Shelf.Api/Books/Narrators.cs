using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

// The voices on the shelf, as Audiobookshelf lists its narrators: each name with the books they read. A book read by
// two counts for each.
public static class Narrators
{
    public static void Map(RouteGroupBuilder books) =>
        books.MapGet("/narrators", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await CountAsync(db, cancellationToken))).WithTags("Books");

    public static async Task<AuthorCount[]> CountAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var given = await db.Books.AsNoTracking().Where(book => book.Narrator != null && book.Narrator != "").Select(book => book.Narrator!).ToListAsync(cancellationToken);
        return [.. given.SelectMany(Names)
            .GroupBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new AuthorCount(group.First(), group.Count()))
            .OrderBy(narrator => narrator.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    // "Ann Reader & Bo Voice" as two names.
    public static IEnumerable<string> Names(string? narrators) => AbsJson.Split(narrators);

    public static bool Reads(string? narrators, string name) => Names(narrators).Any(each => string.Equals(each, name.Trim(), StringComparison.CurrentCultureIgnoreCase));
}

using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// How one book of a reader's stands to another, as the reader says: another edition or translation of the same
// book, one that inspired it, one that answers or argues with it, or simply one to read beside it.
public enum LinkKind
{
    Edition,
    Inspired,
    Answers,
    Contrasts,
    Related,
}

// A line the reader draws between two of their own books. Edition and Contrasts and Related read the same both
// ways; Inspired and Answers have a direction: From was inspired by To, or From answers To.
public sealed class BookLink
{
    public const int MaxNoteLength = 300;
    public const int MaxPerBook = 100;

    public int Id { get; set; }
    public int FromBookId { get; set; }
    public Book? From { get; set; }
    public int ToBookId { get; set; }
    public Book? To { get; set; }
    public LinkKind Kind { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record BookLinkRequest(int ToBookId, LinkKind Kind, [MaxLength(BookLink.MaxNoteLength)] string? Note = null);

// A link as seen from one book: the other book, and what it is to this one.
public sealed record BookLinked(
    int LinkId,
    int BookId,
    string Title,
    string Author,
    string? CoverUrl,
    BookStatus Status,
    LinkKind Kind,
    bool Outgoing,
    string? Note,
    BookFormat? Format = null,
    string? Translator = null,
    string? Narrator = null,
    string? Language = null);

public sealed record MapNode(int Id, string Title, string Author, BookStatus Status, int Depth);

public sealed record MapEdge(int From, int To, LinkKind Kind);

public sealed record LiteraryMap(MapNode[] Books, MapEdge[] Links);

public static class BookLinks
{
    public static void Map(RouteGroupBuilder books)
    {
        books.MapGet("/{id:int}/links", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await db.Books.AnyAsync(book => book.Id == id, cancellationToken) ? TypedResults.Ok(await ForBookAsync(db, id, cancellationToken)) : TypedResults.NotFound());
        books.MapPost("/{id:int}/links", async Task<IResult> (int id, BookLinkRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            await LinkAsync(db, id, request.ToBookId, request.Kind, request.Note, cancellationToken) switch
            {
                (int made, null) => TypedResults.Created($"/books/{id}/links/{made}", new { id = made }),
                (_, { } problem) when problem.Length > 0 => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["ToBookId"] = [problem] }),
                _ => TypedResults.NotFound(),
            });
        books.MapDelete("/{id:int}/links/{linkId:int}", async Task<IResult> (int id, int linkId, ShelfDb db, CancellationToken cancellationToken) =>
            await UnlinkAsync(db, id, linkId, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());
        books.MapGet("/map", async (int? book, ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await MapAsync(db, book, cancellationToken: cancellationToken)));
    }

    // The new link's id; or a problem to tell, empty when either book is not on the reader's shelf.
    public static async Task<(int? Id, string? Problem)> LinkAsync(ShelfDb db, int fromId, int toId, LinkKind kind, string? note, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind))
        {
            return (null, T("Choose how the books are linked."));
        }

        var found = await db.Books.Where(book => book.Id == fromId || book.Id == toId).Select(book => book.Id).ToListAsync(cancellationToken);
        if (!found.Contains(fromId) || !found.Contains(toId))
        {
            return (null, "");
        }

        if (fromId == toId)
        {
            return (null, T("A book can't be linked to itself."));
        }

        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note?.Length > BookLink.MaxNoteLength)
        {
            return (null, T("Keep the note to {0} characters.", BookLink.MaxNoteLength));
        }

        var between = await db.BookLinks.Where(link => (link.FromBookId == fromId && link.ToBookId == toId) || (link.FromBookId == toId && link.ToBookId == fromId))
            .Select(link => new { link.FromBookId, link.Kind }).ToListAsync(cancellationToken);
        if (between.Any(link => link.Kind == kind && (link.FromBookId == fromId || !Directed(kind))))
        {
            return (null, T("Those books are already linked that way."));
        }

        if (await db.BookLinks.CountAsync(link => link.FromBookId == fromId || link.ToBookId == fromId, cancellationToken) >= BookLink.MaxPerBook)
        {
            return (null, T("A book can have up to {0} links.", BookLink.MaxPerBook));
        }

        var made = new BookLink { FromBookId = fromId, ToBookId = toId, Kind = kind, Note = note, CreatedAt = DateTimeOffset.UtcNow };
        db.BookLinks.Add(made);
        await db.SaveChangesAsync(cancellationToken);
        return (made.Id, null);
    }

    public static async Task<bool> UnlinkAsync(ShelfDb db, int bookId, int linkId, CancellationToken cancellationToken = default) =>
        await db.BookLinks.Where(link => link.Id == linkId && (link.FromBookId == bookId || link.ToBookId == bookId)).ExecuteDeleteAsync(cancellationToken) > 0;

    public static bool Directed(LinkKind kind) => kind is LinkKind.Inspired or LinkKind.Answers;

    // The book's links both ways, the other book's details beside each.
    public static async Task<BookLinked[]> ForBookAsync(ShelfDb db, int bookId, CancellationToken cancellationToken = default)
    {
        var outgoing = await db.BookLinks.AsNoTracking().Where(link => link.FromBookId == bookId)
            .Select(link => new { link.Id, Other = link.To!, link.Kind, link.Note, Outgoing = true })
            .ToListAsync(cancellationToken);
        var incoming = await db.BookLinks.AsNoTracking().Where(link => link.ToBookId == bookId)
            .Select(link => new { link.Id, Other = link.From!, link.Kind, link.Note, Outgoing = false })
            .ToListAsync(cancellationToken);
        return outgoing.Concat(incoming)
            .Select(link => new BookLinked(link.Id, link.Other.Id, link.Other.Title, link.Other.Author, link.Other.CoverShown, link.Other.Status, link.Kind, link.Outgoing, link.Note,
                link.Other.Format, link.Other.Translator, link.Other.Narrator, link.Other.Language))
            .OrderBy(link => link.Kind).ThenBy(link => link.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    // What the other book is to this one, in a few words.
    public static string Describe(LinkKind kind, bool outgoing) => (kind, outgoing) switch
    {
        (LinkKind.Edition, _) => T("Another edition"),
        (LinkKind.Inspired, true) => T("Inspired by"),
        (LinkKind.Inspired, false) => T("Inspired by this one"),
        (LinkKind.Answers, true) => T("Answers"),
        (LinkKind.Answers, false) => T("Answered by"),
        (LinkKind.Contrasts, _) => T("A contrast"),
        _ => T("Read beside"),
    };

    // The kinds as the reader chooses them, from the book they are on.
    public static IReadOnlyList<(LinkKind Kind, string Label)> Choices() =>
    [
        (LinkKind.Edition, T("is another edition or translation of")),
        (LinkKind.Inspired, T("was inspired by")),
        (LinkKind.Answers, T("answers or argues with")),
        (LinkKind.Contrasts, T("makes a contrast with")),
        (LinkKind.Related, T("is good to read beside")),
    ];

    // The books around one, out to the given number of steps along the reader's links; or, with no book, every
    // linked book on the shelf. Only the links are read, so a shelf of any size draws quickly.
    public static async Task<LiteraryMap> MapAsync(ShelfDb db, int? centre, int steps = 2, int most = 150, CancellationToken cancellationToken = default)
    {
        var links = await db.BookLinks.AsNoTracking().Select(link => new MapEdge(link.FromBookId, link.ToBookId, link.Kind)).ToListAsync(cancellationToken);
        var depth = new Dictionary<int, int>();
        if (centre is int start)
        {
            if (!await db.Books.AnyAsync(book => book.Id == start, cancellationToken))
            {
                return new LiteraryMap([], []);
            }

            depth[start] = 0;
            var ring = new List<int> { start };
            for (var step = 1; step <= steps && ring.Count > 0 && depth.Count < most; step++)
            {
                var next = new List<int>();
                foreach (var link in links)
                {
                    foreach (var (here, there) in new[] { (link.From, link.To), (link.To, link.From) })
                    {
                        if (ring.Contains(here) && !depth.ContainsKey(there) && depth.Count < most)
                        {
                            depth[there] = step;
                            next.Add(there);
                        }
                    }
                }

                ring = next;
            }
        }
        else
        {
            foreach (var id in links.SelectMany(link => new[] { link.From, link.To }).Distinct().Take(most))
            {
                depth[id] = 0;
            }
        }

        var ids = depth.Keys.ToList();
        var books = await db.Books.AsNoTracking().Where(book => ids.Contains(book.Id))
            .Select(book => new { book.Id, book.Title, book.Author, book.Status })
            .ToListAsync(cancellationToken);
        return new LiteraryMap(
            books.Select(book => new MapNode(book.Id, book.Title, book.Author, book.Status, depth[book.Id])).OrderBy(node => node.Depth).ThenBy(node => node.Title, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            links.Where(link => depth.ContainsKey(link.From) && depth.ContainsKey(link.To)).ToArray());
    }
}

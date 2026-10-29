using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// A queue of things to listen to, in the order the reader puts them: audiobooks and podcast episodes together, as
// Audiobookshelf's playlists hold them. Played, each goes on into the next. A reader's own, seen by no one else.
public sealed class Playlist
{
    public const int MaxNameLength = 80;
    public const int MaxItems = 500;

    public int Id { get; set; }
    public int OwnerId { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<PlaylistItem> Items { get; set; } = [];
}

// One thing in a playlist: a book with a recording, or a podcast episode.
public sealed class PlaylistItem
{
    public int Id { get; set; }
    public int PlaylistId { get; set; }
    public Playlist? Playlist { get; set; }
    public int Position { get; set; }
    public int? BookId { get; set; }
    public Book? Book { get; set; }
    public int? EpisodeId { get; set; }
    public PodcastEpisode? Episode { get; set; }
}

public sealed record PlaylistSummary(int Id, string Name, int Count);

public sealed record PlaylistEntry(int Id, string Title, string Detail, string PlanUrl, string Link, string? Cover, bool Gone);

public sealed record PlaylistView(int Id, string Name, PlaylistEntry[] Items);

public sealed record PlaylistRequest(string? Name);

public sealed record PlaylistAddRequest(int? BookId, int? EpisodeId);

public sealed record PlaylistMoveRequest(int ItemId, int Step);

public static class Playlists
{
    public static void Map(RouteGroupBuilder books)
    {
        books.MapGet("/playlists", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await ListAsync(db, cancellationToken))).WithTags("Playlists");
        books.MapPost("/playlists", async Task<IResult> (PlaylistRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            await CreateAsync(db, request.Name, cancellationToken) is ({ } made, null)
                ? TypedResults.Created($"/books/playlists/{made.Id}", made)
                : TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Name"] = [Problem(request.Name)] })).WithTags("Playlists");
        books.MapGet("/playlists/{id:int}", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await ViewAsync(db, id, cancellationToken) is { } view ? TypedResults.Ok(view) : TypedResults.NotFound()).WithTags("Playlists");
        books.MapPut("/playlists/{id:int}", async Task<IResult> (int id, PlaylistRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            await RenameAsync(db, id, request.Name, cancellationToken) switch
            {
                true => TypedResults.NoContent(),
                false => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Name"] = [Problem(request.Name)] }),
                null => TypedResults.NotFound(),
            }).WithTags("Playlists");
        books.MapDelete("/playlists/{id:int}", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await db.Playlists.Where(item => item.Id == id).ExecuteDeleteAsync(cancellationToken) > 0 ? TypedResults.NoContent() : TypedResults.NotFound()).WithTags("Playlists");
        books.MapPost("/playlists/{id:int}/items", async Task<IResult> (int id, PlaylistAddRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            await AddAsync(db, id, request.BookId, request.EpisodeId, cancellationToken) switch
            {
                null => TypedResults.NoContent(),
                "" => TypedResults.NotFound(),
                var problem => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Item"] = [problem] }),
            }).WithTags("Playlists");
        books.MapDelete("/playlists/{id:int}/items/{itemId:int}", async Task<IResult> (int id, int itemId, ShelfDb db, CancellationToken cancellationToken) =>
            await RemoveAsync(db, id, itemId, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound()).WithTags("Playlists");
        books.MapPost("/playlists/{id:int}/move", async Task<IResult> (int id, PlaylistMoveRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            await MoveAsync(db, id, request.ItemId, request.Step, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound()).WithTags("Playlists");
    }

    public static async Task<PlaylistSummary[]> ListAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        [.. (await db.Playlists.AsNoTracking().Select(list => new { list.Id, list.Name, Count = list.Items.Count }).ToListAsync(cancellationToken))
            .OrderBy(list => list.Name, StringComparer.CurrentCultureIgnoreCase).Select(list => new PlaylistSummary(list.Id, list.Name, list.Count))];

    public static async Task<(PlaylistSummary? Made, string? Problem)> CreateAsync(ShelfDb db, string? name, CancellationToken cancellationToken = default)
    {
        if (Clean(name) is not { } given)
        {
            return (null, Problem(name));
        }

        var list = new Playlist { OwnerId = db.ReaderId, Name = given, CreatedAt = DateTimeOffset.UtcNow };
        db.Playlists.Add(list);
        await db.SaveChangesAsync(cancellationToken);
        return (new PlaylistSummary(list.Id, list.Name, 0), null);
    }

    public static async Task<bool?> RenameAsync(ShelfDb db, int id, string? name, CancellationToken cancellationToken = default)
    {
        if (await db.Playlists.FirstOrDefaultAsync(item => item.Id == id, cancellationToken) is not { } list)
        {
            return null;
        }

        if (Clean(name) is not { } given)
        {
            return false;
        }

        list.Name = given;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // The playlist as it plays: each thing with where its player plan is. A book let go since, or an episode its
    // feed dropped, stays in its place, marked gone, and is skipped when played.
    public static async Task<PlaylistView?> ViewAsync(ShelfDb db, int id, CancellationToken cancellationToken = default)
    {
        if (await db.Playlists.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken) is not { } list)
        {
            return null;
        }

        var readerId = db.ReaderId;
        var items = await db.PlaylistItems.AsNoTracking().Where(item => item.PlaylistId == id).OrderBy(item => item.Position).ThenBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.BookId,
                item.EpisodeId,
                EpisodeTitle = item.Episode == null || item.Episode.Podcast!.OwnerId != readerId ? null : item.Episode.Title,
                Show = item.Episode == null || item.Episode.Podcast!.OwnerId != readerId ? null : item.Episode.Podcast!.Title,
                PodcastId = item.Episode == null || item.Episode.Podcast!.OwnerId != readerId ? (int?)null : item.Episode.PodcastId,
            })
            .ToListAsync(cancellationToken);

        // The books as this reader may open them: their own, lent to them, or shared with them; any other is gone.
        var bookIds = items.Where(item => item.BookId is not null).Select(item => item.BookId!.Value).ToList();
        var shared = bookIds.Count == 0 ? [] : await Lending.SharedIdsAsync(db, cancellationToken);
        var books = await db.Books.IgnoreQueryFilters().AsNoTracking()
            .Where(book => bookIds.Contains(book.Id) && (book.OwnerId == readerId || book.BorrowerId == readerId || shared.Contains(book.Id)))
            .Select(book => new { book.Id, book.OwnerId, book.Title, book.Author, book.AudioStoredName, book.CoverUrl, book.FileCover, book.CoverImage })
            .ToDictionaryAsync(book => book.Id, cancellationToken);
        return new PlaylistView(list.Id, list.Name, [.. items.Select(item => item.BookId is int bookId
            ? books.TryGetValue(bookId, out var book)
                ? new PlaylistEntry(item.Id, book.Title, book.Author, $"/books/{bookId}/audio/plan", book.OwnerId == readerId ? $"/library/{bookId}" : $"/library/{bookId}/listen", Covers.For(book.Id, book.CoverUrl, book.FileCover, book.CoverImage), book.AudioStoredName is null)
                : new PlaylistEntry(item.Id, T("A book no longer on your shelf"), "", $"/books/{bookId}/audio/plan", "/library", null, true)
            : new PlaylistEntry(item.Id, item.EpisodeTitle ?? T("An episode no longer in its feed"), item.Show ?? "", $"/books/podcasts/episodes/{item.EpisodeId}/plan", item.PodcastId is int show ? $"/podcasts/{show}" : "/podcasts", null, item.EpisodeTitle is null))]);
    }

    // Null when added; empty when there's no such playlist, book, or episode of the reader's; else why not.
    public static async Task<string?> AddAsync(ShelfDb db, int id, int? bookId, int? episodeId, CancellationToken cancellationToken = default)
    {
        if (!await db.Playlists.AnyAsync(item => item.Id == id, cancellationToken) || (bookId is null) == (episodeId is null))
        {
            return "";
        }

        // A book the reader may open: their own, lent to them, or shared with them.
        if (bookId is int book)
        {
            if (await Lending.OpenAsync(db, book, cancellationToken) is not { } open)
            {
                return "";
            }

            if (open.Book.AudioStoredName is null)
            {
                return T("A playlist holds things to listen to; this book has no recording.");
            }
        }
        // Episodes carry no reader of their own: one of a podcast the reader follows, never another reader's.
        else if (!await db.PodcastEpisodes.AnyAsync(item => item.Id == episodeId && item.Podcast!.OwnerId == db.ReaderId && db.ReaderId != 0, cancellationToken))
        {
            return "";
        }

        var count = await db.PlaylistItems.CountAsync(item => item.PlaylistId == id, cancellationToken);
        if (count >= Playlist.MaxItems)
        {
            return T("A playlist holds up to {0} things.", Playlist.MaxItems);
        }

        if (await db.PlaylistItems.AnyAsync(item => item.PlaylistId == id && item.BookId == bookId && item.EpisodeId == episodeId, cancellationToken))
        {
            return T("It's in the playlist already.");
        }

        var last = count == 0 ? -1 : await db.PlaylistItems.Where(item => item.PlaylistId == id).MaxAsync(item => item.Position, cancellationToken);
        db.PlaylistItems.Add(new PlaylistItem { PlaylistId = id, BookId = bookId, EpisodeId = episodeId, Position = last + 1 });
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    public static async Task<bool> RemoveAsync(ShelfDb db, int id, int itemId, CancellationToken cancellationToken = default) =>
        await db.Playlists.AnyAsync(item => item.Id == id, cancellationToken)
        && await db.PlaylistItems.Where(item => item.Id == itemId && item.PlaylistId == id).ExecuteDeleteAsync(cancellationToken) > 0;

    // One place up (a step of -1) or down (1).
    public static async Task<bool> MoveAsync(ShelfDb db, int id, int itemId, int step, CancellationToken cancellationToken = default)
    {
        if (!await db.Playlists.AnyAsync(item => item.Id == id, cancellationToken))
        {
            return false;
        }

        var items = await db.PlaylistItems.Where(item => item.PlaylistId == id).OrderBy(item => item.Position).ThenBy(item => item.Id).ToListAsync(cancellationToken);
        var at = items.FindIndex(item => item.Id == itemId);
        if (at < 0)
        {
            return false;
        }

        var to = Math.Clamp(at + Math.Sign(step), 0, items.Count - 1);
        var moving = items[at];
        items.RemoveAt(at);
        items.Insert(to, moving);
        for (var i = 0; i < items.Count; i++)
        {
            items[i].Position = i;
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string? Clean(string? name) => name?.Trim() is { Length: > 0 and <= Playlist.MaxNameLength } given ? given : null;

    private static string Problem(string? name) => T("Give the playlist a name of up to {0} characters.", Playlist.MaxNameLength);
}

using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public enum PlaceKind
{
    Room,
    Bookcase,
    Shelf,
    Box,
    Other,
}

// A place a copy can be: a room, a bookcase in it, a shelf in that, or a box. A copy's place is written out in full
// on the book ("Study › Oak bookcase › Shelf 3"), so a place moved or renamed takes its books' places with it.
public sealed class StoragePlace
{
    public const int MaxNameLength = 60;
    public const int MaxNoteLength = 200;

    public int Id { get; set; }
    public int OwnerId { get; set; }
    public int? ParentId { get; set; }
    public required string Name { get; set; }
    public PlaceKind Kind { get; set; }

    // A place books are only in for a time, as a box in a move or a storage unit: they keep their usual place.
    public bool Temporary { get; set; }

    // A box's number, and the place its books go when it is unpacked.
    public int? Number { get; set; }
    public int? DestinationId { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PackedAt { get; set; }
    public DateTimeOffset? UnpackedAt { get; set; }
}

public sealed record PlaceNode(int Id, int? ParentId, string Name, string Path, PlaceKind Kind, bool Temporary, int? Number, int? DestinationId, string? Destination,
    string? Note, int Here, int Within, DateTimeOffset? PackedAt, DateTimeOffset? UnpackedAt);

public sealed record PlaceMap(PlaceNode[] Places, PlaceCount[] Loose);

public sealed record StoragePlaceRequest(string Name, PlaceKind Kind = PlaceKind.Room, int? ParentId = null, bool Temporary = false, int? DestinationId = null, string? Note = null);

public sealed record StoragePlaceMove(int[] BookIds, int? PlaceId);

public static class PlaceTree
{
    // Between a place and the place within it, in a copy's place written out.
    public const string Separator = " › ";
    public const int MaxPlaces = 2000;
    public const int MaxDepth = 6;

    public static void Map(RouteGroupBuilder books)
    {
        var places = books.MapGroup("/storage").WithTags("Places");
        places.MapGet("/", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await MapAsync(db, cancellationToken)));
        places.MapPost("/", async Task<IResult> (StoragePlaceRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            await CreateAsync(db, request, cancellationToken) switch
            {
                (StoragePlace made, null) => TypedResults.Created($"/books/storage/{made.Id}", new { made.Id, Path = await PathAsync(db, made.Id, cancellationToken) }),
                (_, var problem) => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Name"] = [problem!] }),
            });
        places.MapPut("/{id:int}", async Task<IResult> (int id, StoragePlaceRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            await ChangeAsync(db, id, request, cancellationToken) switch
            {
                null => TypedResults.NoContent(),
                var problem when problem == NotFound => TypedResults.NotFound(),
                var problem => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Name"] = [problem] }),
            });
        places.MapDelete("/{id:int}", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await RemoveAsync(db, id, cancellationToken) switch
            {
                null => TypedResults.NoContent(),
                var problem when problem == NotFound => TypedResults.NotFound(),
                var problem => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Id"] = [problem] }),
            });
        places.MapPost("/move", async Task<IResult> (StoragePlaceMove request, ShelfDb db, CancellationToken cancellationToken) =>
            await MoveBooksAsync(db, request.BookIds ?? [], request.PlaceId, cancellationToken) is int moved ? TypedResults.Ok(new { moved }) : TypedResults.NotFound());
        places.MapPost("/put-back", async (StoragePlaceMove request, ShelfDb db, CancellationToken cancellationToken) =>
            TypedResults.Ok(new { moved = await PutBackAsync(db, request.BookIds ?? [], cancellationToken) }));
        places.MapPost("/adopt", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(new { made = await AdoptAsync(db, cancellationToken) }));
    }

    private static readonly string NotFound = "\0";

    public static string KindLabel(PlaceKind kind) => kind switch
    {
        PlaceKind.Room => T("Room"),
        PlaceKind.Bookcase => T("Bookcase"),
        PlaceKind.Shelf => T("Shelf"),
        PlaceKind.Box => T("Box"),
        _ => T("Other place"),
    };

    // Every place with its full path and how many copies are at it and within it, and the places copies name that
    // are not (yet) places here.
    public static async Task<PlaceMap> MapAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var places = await db.StoragePlaces.AsNoTracking().ToListAsync(cancellationToken);
        var paths = Paths(places);
        var counts = (await db.Books.AsNoTracking().Where(book => book.Location != null).GroupBy(book => book.Location!)
            .Select(group => new { group.Key, Count = group.Count() }).ToListAsync(cancellationToken))
            .GroupBy(item => item.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Count), StringComparer.OrdinalIgnoreCase);
        var nodes = places.Select(place =>
        {
            var path = paths[place.Id];
            var within = counts.Where(pair => pair.Key.Equals(path, StringComparison.OrdinalIgnoreCase) || pair.Key.StartsWith(path + Separator, StringComparison.OrdinalIgnoreCase)).Sum(pair => pair.Value);
            return new PlaceNode(place.Id, place.ParentId, place.Name, path, place.Kind, place.Temporary, place.Number, place.DestinationId,
                place.DestinationId is int to && paths.TryGetValue(to, out var destination) ? destination : null, place.Note,
                counts.GetValueOrDefault(path), within, place.PackedAt, place.UnpackedAt);
        }).OrderBy(node => node.Path, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var known = paths.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loose = counts.Where(pair => !known.Contains(pair.Key)).Select(pair => new PlaceCount(pair.Key, pair.Value))
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        return new PlaceMap(nodes, loose);
    }

    public static Dictionary<int, string> Paths(IReadOnlyCollection<StoragePlace> places)
    {
        var byId = places.ToDictionary(place => place.Id);
        var paths = new Dictionary<int, string>();
        foreach (var place in places)
        {
            var names = new List<string>();
            var at = place;
            var seen = new HashSet<int>();
            while (at is not null && seen.Add(at.Id))
            {
                names.Insert(0, at.Name);
                at = at.ParentId is int parent ? byId.GetValueOrDefault(parent) : null;
            }

            paths[place.Id] = string.Join(Separator, names);
        }

        return paths;
    }

    public static async Task<string?> PathAsync(ShelfDb db, int id, CancellationToken cancellationToken = default)
    {
        var places = await db.StoragePlaces.AsNoTracking().ToListAsync(cancellationToken);
        return Paths(places).GetValueOrDefault(id);
    }

    public static async Task<(StoragePlace? Made, string? Problem)> CreateAsync(ShelfDb db, StoragePlaceRequest request, CancellationToken cancellationToken = default)
    {
        var places = await db.StoragePlaces.ToListAsync(cancellationToken);
        if (places.Count >= MaxPlaces)
        {
            return (null, T("A shelf can have up to {0} places.", MaxPlaces));
        }

        var kind = Enum.IsDefined(request.Kind) ? request.Kind : PlaceKind.Other;
        int? number = kind == PlaceKind.Box ? places.Where(place => place.Number is not null).Select(place => place.Number!.Value).DefaultIfEmpty(0).Max() + 1 : null;
        var name = Clean(request.Name) ?? (number is int box ? T("Box {0}", box) : null);
        var place = new StoragePlace
        {
            OwnerId = db.ReaderId,
            Name = name ?? "",
            Kind = kind,
            ParentId = request.ParentId,
            Temporary = request.Temporary || kind == PlaceKind.Box,
            Number = number,
            DestinationId = request.DestinationId,
            Note = Note(request.Note),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        if (Check(places, place) is { } problem)
        {
            return (null, problem);
        }

        db.StoragePlaces.Add(place);
        await db.SaveChangesAsync(cancellationToken);
        return (place, null);
    }

    // Renamed, moved under another place, or changed: its copies' places, and those within it, are written anew.
    public static async Task<string?> ChangeAsync(ShelfDb db, int id, StoragePlaceRequest request, CancellationToken cancellationToken = default)
    {
        var places = await db.StoragePlaces.ToListAsync(cancellationToken);
        var place = places.FirstOrDefault(item => item.Id == id);
        if (place is null)
        {
            return NotFound;
        }

        var before = Paths(places);
        var (name, parent, destination) = (place.Name, place.ParentId, place.DestinationId);
        place.Name = Clean(request.Name) ?? "";
        place.ParentId = request.ParentId;
        place.DestinationId = request.DestinationId == id ? null : request.DestinationId;
        if (Check(places, place) is { } problem)
        {
            (place.Name, place.ParentId, place.DestinationId) = (name, parent, destination);
            return problem;
        }

        place.Kind = Enum.IsDefined(request.Kind) ? request.Kind : place.Kind;
        place.Temporary = request.Temporary || place.Kind == PlaceKind.Box;
        place.Note = Note(request.Note);
        var after = Paths(places);
        await RewriteAsync(db, places.Select(item => (before[item.Id], after[item.Id])).Where(pair => pair.Item1 != pair.Item2).ToList(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    // A place with places within it stays; its copies keep their place written out, now a place by name alone.
    public static async Task<string?> RemoveAsync(ShelfDb db, int id, CancellationToken cancellationToken = default)
    {
        var place = await db.StoragePlaces.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (place is null)
        {
            return NotFound;
        }

        if (await db.StoragePlaces.AnyAsync(item => item.ParentId == id, cancellationToken))
        {
            return T("Move or remove the places within it first.");
        }

        await db.StoragePlaces.Where(item => item.DestinationId == id).ExecuteUpdateAsync(set => set.SetProperty(item => item.DestinationId, (int?)null), cancellationToken);
        db.StoragePlaces.Remove(place);
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    // Copies moved to a place, or to none. Into a place for a time, each keeps where it usually lives; into a
    // lasting place, that is where it lives now.
    public static async Task<int?> MoveBooksAsync(ShelfDb db, IReadOnlyCollection<int> bookIds, int? placeId, CancellationToken cancellationToken = default)
    {
        StoragePlace? place = null;
        string? path = null;
        if (placeId is int id)
        {
            var places = await db.StoragePlaces.AsNoTracking().ToListAsync(cancellationToken);
            place = places.FirstOrDefault(item => item.Id == id);
            if (place is null)
            {
                return null;
            }

            path = Paths(places)[id];
        }

        var temporary = await TemporaryPathsAsync(db, cancellationToken);
        var moved = 0;
        foreach (var chunk in bookIds.Distinct().Chunk(500))
        {
            var books = await db.Books.Where(book => chunk.Contains(book.Id)).ToListAsync(cancellationToken);
            foreach (var book in books)
            {
                if (Put(book, path, place?.Temporary == true, temporary))
                {
                    moved++;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return moved;
    }

    // A copy put at a place: one only for a time remembers where the copy usually lives, unless that was for a
    // time too.
    public static bool Put(Book book, string? path, bool forATime, IReadOnlySet<string> temporary)
    {
        if (string.Equals(book.Location, path, StringComparison.Ordinal))
        {
            return false;
        }

        if (forATime)
        {
            if (book.HomeLocation is null && book.Location is { } usual && !IsUnder(usual, temporary))
            {
                book.HomeLocation = usual;
            }
        }
        else
        {
            book.HomeLocation = null;
        }

        book.Location = path;
        return true;
    }

    // Copies away for a time taken back to where they usually live.
    public static async Task<int> PutBackAsync(ShelfDb db, IReadOnlyCollection<int> bookIds, CancellationToken cancellationToken = default)
    {
        var moved = 0;
        foreach (var chunk in bookIds.Distinct().Chunk(500))
        {
            var books = await db.Books.Where(book => chunk.Contains(book.Id) && book.HomeLocation != null).ToListAsync(cancellationToken);
            foreach (var book in books)
            {
                (book.Location, book.HomeLocation) = (book.HomeLocation, null);
                moved++;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return moved;
    }

    public static async Task<HashSet<string>> TemporaryPathsAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var places = await db.StoragePlaces.AsNoTracking().ToListAsync(cancellationToken);
        var paths = Paths(places);
        return places.Where(place => place.Temporary).Select(place => paths[place.Id]).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsUnder(string location, IReadOnlySet<string> paths) =>
        paths.Contains(location) || paths.Any(path => location.StartsWith(path + Separator, StringComparison.OrdinalIgnoreCase));

    // The places copies already name made places here, each part of a written-out place its own, in its place.
    public static async Task<int> AdoptAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var map = await MapAsync(db, cancellationToken);
        var places = await db.StoragePlaces.ToListAsync(cancellationToken);
        var made = 0;
        foreach (var loose in map.Loose)
        {
            int? parent = null;
            foreach (var part in loose.Name.Split(Separator.Trim(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var name = part.Length > StoragePlace.MaxNameLength ? part[..StoragePlace.MaxNameLength] : part;
                var found = places.FirstOrDefault(place => place.ParentId == parent && place.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (found is null)
                {
                    if (places.Count >= MaxPlaces)
                    {
                        break;
                    }

                    found = new StoragePlace { OwnerId = db.ReaderId, Name = name, ParentId = parent, Kind = parent is null ? PlaceKind.Room : PlaceKind.Other, CreatedAt = DateTimeOffset.UtcNow };
                    db.StoragePlaces.Add(found);
                    await db.SaveChangesAsync(cancellationToken);
                    places.Add(found);
                    made++;
                }

                parent = found.Id;
            }
        }

        // A place written a little differently ("study" for "Study") takes the place's own spelling.
        var paths = Paths(places);
        var spelled = paths.Values.ToDictionary(path => path, StringComparer.OrdinalIgnoreCase);
        var books = await db.Books.Where(book => book.Location != null).ToListAsync(cancellationToken);
        foreach (var book in books)
        {
            var tidy = string.Join(Separator, book.Location!.Split(Separator.Trim(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (spelled.TryGetValue(tidy, out var path) && path != book.Location)
            {
                book.Location = path;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return made;
    }

    private static async Task RewriteAsync(ShelfDb db, List<(string Before, string After)> moves, CancellationToken cancellationToken)
    {
        if (moves.Count == 0)
        {
            return;
        }

        // The longest first, so a place within another is taken as itself.
        moves = [.. moves.OrderByDescending(move => move.Before.Length)];
        // Only the copies at or within a place that moved, not the whole shelf.
        var books = new List<Book>();
        foreach (var (before, _) in moves)
        {
            var within = before + Separator;
            books.AddRange(await db.Books.Where(book => book.Location == before || book.Location!.StartsWith(within) || book.HomeLocation == before || book.HomeLocation!.StartsWith(within))
                .ToListAsync(cancellationToken));
        }

        books = [.. books.DistinctBy(book => book.Id)];
        string? Moved(string? location)
        {
            if (location is null)
            {
                return null;
            }

            foreach (var (before, after) in moves)
            {
                if (location.Equals(before, StringComparison.OrdinalIgnoreCase))
                {
                    return Fit(after);
                }

                if (location.StartsWith(before + Separator, StringComparison.OrdinalIgnoreCase))
                {
                    return Fit(after + location[before.Length..]);
                }
            }

            return location;
        }

        foreach (var book in books)
        {
            (book.Location, book.HomeLocation) = (Moved(book.Location), Moved(book.HomeLocation));
        }
    }

    private static string Fit(string path) => path.Length <= 80 ? path : path[..80];

    private static string? Check(List<StoragePlace> places, StoragePlace place)
    {
        if (string.IsNullOrWhiteSpace(place.Name))
        {
            return T("Give the place a name.");
        }

        if (place.Name.Contains(Separator.Trim(), StringComparison.Ordinal))
        {
            return T("A place's name can't have “›” in it; put the place within another instead.");
        }

        if (place.ParentId is int parentId)
        {
            var byId = places.ToDictionary(item => item.Id);
            if (!byId.ContainsKey(parentId))
            {
                return T("That place isn't on your shelf.");
            }

            var depth = 1;
            for (var at = byId[parentId]; at is not null; at = at.ParentId is int up ? byId.GetValueOrDefault(up) : null)
            {
                if (at.Id == place.Id)
                {
                    return T("A place can't be within itself.");
                }

                if (++depth > MaxDepth)
                {
                    return T("Places go at most {0} deep.", MaxDepth);
                }
            }
        }

        // A destination is one of the reader's own places too: another reader's would block them removing it.
        if (place.DestinationId is int destinationId && !places.Any(item => item.Id == destinationId))
        {
            return T("That place isn't on your shelf.");
        }

        if (places.Any(item => item != place && item.Id != place.Id && item.ParentId == place.ParentId && item.Name.Equals(place.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return T("There is already a place called {0} there.", place.Name);
        }

        var all = places.Contains(place) ? places : [.. places, place];
        if (place.Id == 0)
        {
            place.Id = -1;
        }

        var tooLong = Paths(all).Values.Any(path => path.Length > 80);
        if (place.Id == -1)
        {
            place.Id = 0;
        }

        return tooLong ? T("Written out in full, a place takes at most 80 characters; use shorter names.") : null;
    }

    private static string? Clean(string? name)
    {
        var text = string.IsNullOrWhiteSpace(name) ? null : string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text is { Length: > StoragePlace.MaxNameLength } ? text[..StoragePlace.MaxNameLength] : text;
    }

    private static string? Note(string? note)
    {
        var text = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        return text is { Length: > StoragePlace.MaxNoteLength } ? text[..StoragePlace.MaxNoteLength] : text;
    }
}

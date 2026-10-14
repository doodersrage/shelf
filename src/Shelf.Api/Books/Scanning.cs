using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public enum ScanKind
{
    // Books scanned one after another, to be added together.
    Add,

    // A place on the shelves checked against the catalog: what is there, what is missing, what is elsewhere.
    Check,
}

// A run of scanning, kept so it can be paused and taken up again on any device: the codes read, in order, and for
// a check, the books seen.
public sealed class ScanSession
{
    public const int MaxCodes = 2000;

    public int Id { get; set; }
    public int OwnerId { get; set; }
    public ScanKind Kind { get; set; }
    public string? Location { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    // ISBNs, a line each, in the order read.
    public string Codes { get; set; } = "";

    // Books seen in a check, by id, comma-separated.
    public string Seen { get; set; } = "";

    public IReadOnlyList<string> CodeList => Codes.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public HashSet<int> SeenIds => [.. Seen.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)];
}

public sealed record ScanSessionView(int Id, ScanKind Kind, string? Location, DateTimeOffset StartedAt, string[] Codes, int[] Seen);

// What one code read in a check turned out to be.
public enum ScanFinding
{
    // A book that belongs here, now seen.
    Here,

    // Seen already in this check.
    Again,

    // On the shelf, but recorded at another place (or none).
    Elsewhere,

    // Not in the catalog at all.
    Unknown,
}

public sealed record ScanCheck(string Isbn, ScanFinding Finding, int? BookId, string? Title, string? Location);

public sealed record CheckedBook(int Id, string Title, string Author, string? Isbn, bool Seen);

public sealed record ScanAddRequest(string[] Isbns, string? Location, BookStatus Status = BookStatus.Want, BookFormat? Format = null);

public sealed record ScanAdded(int Added, int Skipped, int? BatchId);

public sealed record ScanFinishRequest(bool TagMissing);

public sealed record ScanFinished(int Seen, int Missing, int? BatchId);

public static class Scanning
{
    public const string MissingTag = "missing";

    public static void Map(RouteGroupBuilder books)
    {
        var scans = books.MapGroup("/scans").WithTags("Scanning");
        scans.MapGet("/", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await OpenAsync(db, cancellationToken)));
        scans.MapPost("/", async (ScanStart request, ShelfDb db, CancellationToken cancellationToken) =>
            TypedResults.Ok(View(await StartAsync(db, request.Kind, request.Location, cancellationToken))));
        scans.MapPost("/{id:int}/codes", async Task<IResult> (int id, ScanCode request, ShelfDb db, CancellationToken cancellationToken) =>
            await ReadAsync(db, id, request.Isbn, cancellationToken) switch
            {
                null => TypedResults.NotFound(),
                var found => TypedResults.Ok(found),
            });
        scans.MapPost("/{id:int}/add", async Task<IResult> (int id, ScanAddRequest request, ShelfDb db, IBookLookup lookup, CancellationToken cancellationToken) =>
            await AddAsync(db, lookup, id, request, cancellationToken) is { } added ? TypedResults.Ok(added) : TypedResults.NotFound());
        scans.MapGet("/{id:int}/books", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await ExpectedAsync(db, id, cancellationToken) is { } expected ? TypedResults.Ok(expected) : TypedResults.NotFound());
        scans.MapGet("/places", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await PlacesAsync(db, cancellationToken)));
        scans.MapPost("/{id:int}/seen/{bookId:int}", async Task<Results<NoContent, NotFound>> (int id, int bookId, bool? moveHere, ShelfDb db, CancellationToken cancellationToken) =>
            await MarkSeenAsync(db, id, bookId, moveHere == true, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());
        scans.MapDelete("/{id:int}/codes/{isbn}", async Task<Results<NoContent, NotFound>> (int id, string isbn, ShelfDb db, CancellationToken cancellationToken) =>
            await UnreadAsync(db, id, isbn, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());
        scans.MapDelete("/{id:int}", async (int id, ShelfDb db, CancellationToken cancellationToken) =>
        {
            await DropAsync(db, id, cancellationToken);
            return TypedResults.NoContent();
        });
        scans.MapPost("/{id:int}/finish", async Task<IResult> (int id, ScanFinishRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            await FinishAsync(db, id, request.TagMissing, cancellationToken) is { } finished ? TypedResults.Ok(finished) : TypedResults.NotFound());
    }

    public sealed record ScanStart(ScanKind Kind, string? Location);

    public sealed record ScanCode(string Isbn);

    public static ScanSessionView View(ScanSession session) =>
        new(session.Id, session.Kind, session.Location, session.StartedAt, [.. session.CodeList], [.. session.SeenIds]);

    public static async Task<ScanSessionView[]> OpenAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        [.. (await db.ScanSessions.AsNoTracking().Where(session => session.FinishedAt == null).OrderByDescending(session => session.Id).ToListAsync(cancellationToken)).Select(View)];

    public static async Task<ScanSession> StartAsync(ShelfDb db, ScanKind kind, string? location, CancellationToken cancellationToken = default)
    {
        var place = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
        place = place is { Length: > 80 } ? place[..80] : place;
        var session = new ScanSession { OwnerId = db.ReaderId, Kind = kind, Location = place, StartedAt = DateTimeOffset.UtcNow };
        db.ScanSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return session;
    }

    public static Task<ScanSession?> FindAsync(ShelfDb db, int id, CancellationToken cancellationToken = default) =>
        db.ScanSessions.FirstOrDefaultAsync(session => session.Id == id && session.FinishedAt == null, cancellationToken);

    // One code read. An ISBN-10 is taken as its ISBN-13; anything else is not a book's code. A code read twice in a
    // row (the camera sees the same barcode for a moment) is kept once.
    public static async Task<ScanCheck?> ReadAsync(ShelfDb db, int sessionId, string? code, CancellationToken cancellationToken = default)
    {
        var session = await FindAsync(db, sessionId, cancellationToken);
        if (session is null || BookRules.NormalizeIsbn(code) is not { } isbn)
        {
            return null;
        }

        var thirteen = isbn.Length == 10 ? ToThirteen(isbn) : isbn;
        var book = await db.Books.AsNoTracking().Where(item => item.Isbn == thirteen || item.Isbn == isbn)
            .OrderBy(item => item.Location == session.Location ? 0 : 1).ThenBy(item => item.Id)
            .Select(item => new { item.Id, item.Title, item.Location }).FirstOrDefaultAsync(cancellationToken);
        var codes = session.CodeList;
        if (codes.Count < ScanSession.MaxCodes && (session.Kind == ScanKind.Check || !codes.Contains(thirteen)))
        {
            session.Codes += thirteen + "\n";
        }

        ScanCheck result;
        if (book is null)
        {
            result = new ScanCheck(thirteen, ScanFinding.Unknown, null, null, null);
        }
        else if (session.Kind == ScanKind.Add || !SamePlace(book.Location, session.Location))
        {
            result = new ScanCheck(thirteen, ScanFinding.Elsewhere, book.Id, book.Title, book.Location);
        }
        else
        {
            var seen = session.SeenIds;
            result = new ScanCheck(thirteen, seen.Contains(book.Id) ? ScanFinding.Again : ScanFinding.Here, book.Id, book.Title, book.Location);
            if (seen.Add(book.Id))
            {
                session.Seen = string.Join(',', seen);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    // A book without a barcode, ticked off by hand, or one from elsewhere put here.
    public static async Task<bool> MarkSeenAsync(ShelfDb db, int sessionId, int bookId, bool moveHere, CancellationToken cancellationToken = default)
    {
        var session = await FindAsync(db, sessionId, cancellationToken);
        var book = await db.Books.FirstOrDefaultAsync(item => item.Id == bookId, cancellationToken);
        if (session is null || book is null)
        {
            return false;
        }

        if (moveHere)
        {
            book.Location = session.Location;
        }

        var seen = session.SeenIds;
        seen.Add(bookId);
        session.Seen = string.Join(',', seen);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public static async Task<bool> UnreadAsync(ShelfDb db, int sessionId, string isbn, CancellationToken cancellationToken = default)
    {
        var session = await FindAsync(db, sessionId, cancellationToken);
        if (session is null)
        {
            return false;
        }

        session.Codes = string.Concat(session.CodeList.Where(code => code != isbn).Select(code => code + "\n"));
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // The books a check expects at its place, and whether each has been seen.
    public static async Task<CheckedBook[]?> ExpectedAsync(ShelfDb db, int sessionId, CancellationToken cancellationToken = default)
    {
        var session = await db.ScanSessions.AsNoTracking().FirstOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session is null || session.Kind != ScanKind.Check)
        {
            return null;
        }

        var seen = session.SeenIds;
        var place = session.Location;
        var books = await db.Books.AsNoTracking()
            .Where(book => place == null ? book.Location == null && book.EbookStoredName == null && book.AudioStoredName == null : book.Location == place)
            .Select(book => new { book.Id, book.Title, book.Author, book.Isbn })
            .ToListAsync(cancellationToken);
        return [.. books
            .Select(book => new CheckedBook(book.Id, book.Title, book.Author, book.Isbn, seen.Contains(book.Id)))
            .OrderBy(book => book.Seen).ThenBy(book => book.Author, StringComparer.CurrentCultureIgnoreCase).ThenBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase)];
    }

    // The books read in an adding run, made together so they can be undone together: the codes the page sends, which
    // leaves out those already on the shelf unless the reader wants a second copy. A code no catalog knows stays in
    // the run, to be entered by hand.
    public static async Task<ScanAdded?> AddAsync(ShelfDb db, IBookLookup lookup, int sessionId, ScanAddRequest request, CancellationToken cancellationToken = default)
    {
        var session = await FindAsync(db, sessionId, cancellationToken);
        if (session is null || session.Kind != ScanKind.Add)
        {
            return null;
        }

        var isbns = (request.Isbns ?? []).Select(BookRules.NormalizeIsbn).OfType<string>().Distinct().Take(ScanSession.MaxCodes).ToList();
        var location = string.IsNullOrWhiteSpace(request.Location) ? session.Location : request.Location.Trim();
        location = location is { Length: > 80 } ? location[..80] : location;
        var status = Enum.IsDefined(request.Status) ? request.Status : BookStatus.Want;
        var (added, skipped) = (0, 0);
        var done = new HashSet<string>();
        int? batchId = null;
        foreach (var isbn in isbns)
        {
            var match = await lookup.FindAsync(isbn, null, null, cancellationToken);
            if (match is null || string.IsNullOrWhiteSpace(match.Title) || string.IsNullOrWhiteSpace(match.Author))
            {
                skipped++;
                continue;
            }

            if (batchId is null)
            {
                var batch = await History.StartAsync(db, BatchKind.Scan, T("Scanned in at {0}", location ?? T("no place")), cancellationToken);
                batchId = batch.Id;
            }

            using var _ = History.Use(batchId);
            var book = new Book
            {
                Title = Cut(match.Title.Trim(), 200),
                Author = Cut(match.Author.Trim(), 200),
                Status = status,
                Isbn = isbn,
                Location = location,
                Format = request.Format ?? match.Format,
                AddedAt = DateTimeOffset.UtcNow,
                AcquiredOn = null,
            };
            var tags = new List<string>();
            BookRules.FillEmpty(book, match, tags);
            if (status == BookStatus.Finished)
            {
                book.FinishedOn = DateOnly.FromDateTime(DateTime.UtcNow);
            }

            db.Books.Add(book);
            await BookRules.SyncTagsAsync(db, book, tags, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            done.Add(isbn);
            added++;
        }

        session.Codes = string.Concat(session.CodeList.Where(code => !done.Contains(code)).Select(code => code + "\n"));
        if (session.Codes.Length == 0)
        {
            session.FinishedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        await BookRules.RemoveUnusedTagsAsync(db, cancellationToken);
        return new ScanAdded(added, skipped, batchId);
    }

    // The check is done: the books not seen can be tagged "missing", together, so the tag can be undone at once.
    public static async Task<ScanFinished?> FinishAsync(ShelfDb db, int sessionId, bool tagMissing, CancellationToken cancellationToken = default)
    {
        var session = await FindAsync(db, sessionId, cancellationToken);
        if (session is null)
        {
            return null;
        }

        int? batchId = null;
        var missing = 0;
        var seen = session.SeenIds;
        if (session.Kind == ScanKind.Check && await ExpectedAsync(db, sessionId, cancellationToken) is { } expected)
        {
            var notSeen = expected.Where(book => !book.Seen).Select(book => book.Id).ToList();
            missing = notSeen.Count;
            if (tagMissing && notSeen.Count > 0)
            {
                var batch = await History.StartAsync(db, BatchKind.Edit, T("Not found at {0}: {1} tagged missing", session.Location ?? T("no place"), notSeen.Count), cancellationToken);
                batchId = batch.Id;
                using var _ = History.Use(batch.Id);
                var tag = await db.Tags.FirstOrDefaultAsync(item => item.Name == MissingTag, cancellationToken) ?? db.Tags.Add(new Tag { Name = MissingTag }).Entity;
                foreach (var chunk in notSeen.Chunk(500))
                {
                    var books = await db.Books.Include(book => book.Tags).Where(book => chunk.Contains(book.Id)).ToListAsync(cancellationToken);
                    foreach (var book in books.Where(book => book.Tags.All(item => item.Name != MissingTag) && book.Tags.Count < BookRules.MaxTags))
                    {
                        book.Tags.Add(tag);
                    }

                    await db.SaveChangesAsync(cancellationToken);
                }
            }
        }

        session.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return new ScanFinished(seen.Count, missing, batchId);
    }

    public static async Task DropAsync(ShelfDb db, int sessionId, CancellationToken cancellationToken = default) =>
        await db.ScanSessions.Where(session => session.Id == sessionId).ExecuteDeleteAsync(cancellationToken);

    // The places the reader's paper books are kept, for choosing which to check.
    public static async Task<string[]> PlacesAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        [.. (await db.Books.AsNoTracking().Where(book => book.Location != null).Select(book => book.Location!).Distinct().ToListAsync(cancellationToken))
            .Order(StringComparer.CurrentCultureIgnoreCase)];

    private static bool SamePlace(string? one, string? other) =>
        string.Equals(one?.Trim(), other?.Trim(), StringComparison.CurrentCultureIgnoreCase);

    // 978 and the first nine digits, with the check digit worked out again.
    public static string ToThirteen(string isbn10)
    {
        var digits = "978" + isbn10[..9];
        var sum = digits.Select((digit, at) => (digit - '0') * (at % 2 == 0 ? 1 : 3)).Sum();
        return digits + (char)('0' + (10 - sum % 10) % 10);
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}

using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// What Shelf tells Hardcover about one book: where the reader is with it, their stars, and when they read it.
public sealed record HardcoverStatus(int StatusId, int? Rating, DateOnly? StartedOn, DateOnly? FinishedOn);

// A book on the reader's Hardcover shelf, as Hardcover has it.
public sealed record HardcoverShelfBook(int UserBookId, int BookId, string Title, string Author, string? Isbn, HardcoverStatus Status);

public interface IHardcoverClient
{
    // The account's name when Hardcover takes the token; null when it does not.
    Task<string?> WhoAsync(string token, CancellationToken cancellationToken);

    // Hardcover's id for a book, by ISBN first, then by title and author.
    Task<int?> FindAsync(string token, string? isbn, string title, string author, CancellationToken cancellationToken);

    // Puts the book on the account's shelf, or updates it there; returns Hardcover's id for the reader's copy.
    Task<int?> SaveAsync(string token, int bookId, int? userBookId, HardcoverStatus status, CancellationToken cancellationToken);

    // Every book on the account's shelf there; null when Hardcover could not be read.
    Task<IReadOnlyList<HardcoverShelfBook>?> ShelfAsync(string token, CancellationToken cancellationToken);
}

// Hardcover's GraphQL API, with the reader's own token from hardcover.app/account/api.
public sealed class HardcoverClient(HttpClient http) : IHardcoverClient
{
    private const string Endpoint = "https://api.hardcover.app/v1/graphql";

    public async Task<string?> WhoAsync(string token, CancellationToken cancellationToken)
    {
        var data = await AskAsync(token, "query { me { username } }", null, cancellationToken);
        // "me" comes as a list of one account, or as the account itself.
        if (data is not { } found || !found.TryGetProperty("me", out var me))
        {
            return null;
        }

        var account = me.ValueKind == JsonValueKind.Array ? (me.GetArrayLength() > 0 ? me[0] : default) : me;
        return account.ValueKind == JsonValueKind.Object && account.TryGetProperty("username", out var name) ? name.GetString() ?? "" : null;
    }

    public async Task<int?> FindAsync(string token, string? isbn, string title, string author, CancellationToken cancellationToken)
    {
        if (isbn is { Length: 10 or 13 })
        {
            var field = isbn.Length == 13 ? "isbn_13" : "isbn_10";
            var byIsbn = await AskAsync(token, $"query Edition($isbn: String!) {{ editions(where: {{{field}: {{_eq: $isbn}}}}, limit: 1) {{ book_id }} }}", new { isbn }, cancellationToken);
            if (byIsbn?.GetProperty("editions") is { ValueKind: JsonValueKind.Array } editions && editions.GetArrayLength() > 0 && editions[0].GetProperty("book_id").TryGetInt32(out var found))
            {
                return found;
            }
        }

        var search = await AskAsync(token, "query Search($q: String!) { search(query: $q, query_type: \"Book\", per_page: 5) { results } }", new { q = $"{title} {author}" }, cancellationToken);
        if (search?.GetProperty("search").GetProperty("results") is not { } results)
        {
            return null;
        }

        // The search answers with its own results document: hits, each with the book.
        var hits = results.ValueKind == JsonValueKind.String ? JsonDocument.Parse(results.GetString()!).RootElement : results;
        if (!hits.TryGetProperty("hits", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var hit in list.EnumerateArray())
        {
            var document = hit.GetProperty("document");
            var named = document.TryGetProperty("title", out var hitTitle) ? hitTitle.GetString() : null;
            if (Same(named, title) && document.TryGetProperty("id", out var id))
            {
                return id.ValueKind == JsonValueKind.Number ? id.GetInt32() : int.Parse(id.GetString()!, CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    public async Task<int?> SaveAsync(string token, int bookId, int? userBookId, HardcoverStatus status, CancellationToken cancellationToken)
    {
        int? id = userBookId;
        if (id is null)
        {
            var made = await AskAsync(token, "mutation Add($object: UserBookCreateInput!) { insert_user_book(object: $object) { id error } }",
                new { @object = new { book_id = bookId, status_id = status.StatusId, rating = status.Rating } }, cancellationToken);
            id = made?.GetProperty("insert_user_book") is { ValueKind: JsonValueKind.Object } added && added.TryGetProperty("id", out var newId) && newId.ValueKind == JsonValueKind.Number ? newId.GetInt32() : null;
        }
        else
        {
            var changed = await AskAsync(token, "mutation Change($id: Int!, $object: UserBookUpdateInput!) { update_user_book(id: $id, object: $object) { id error } }",
                new { id, @object = new { status_id = status.StatusId, rating = status.Rating } }, cancellationToken);
            if (changed is null)
            {
                return null;
            }
        }

        if (id is int copy && (status.StartedOn is not null || status.FinishedOn is not null))
        {
            await AskAsync(token, "mutation Read($id: Int!, $read: DatesReadInput!) { insert_user_book_read(user_book_id: $id, user_book_read: $read) { id error } }",
                new { id = copy, read = new { started_at = status.StartedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), finished_at = status.FinishedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) } }, cancellationToken);
        }

        return id;
    }

    public async Task<IReadOnlyList<HardcoverShelfBook>?> ShelfAsync(string token, CancellationToken cancellationToken)
    {
        const string Query = """
            query Shelf($offset: Int!) { me { user_books(limit: 100, offset: $offset, order_by: {id: asc}) {
              id book_id status_id rating
              book { title contributions(limit: 1) { author { name } } }
              edition { isbn_13 isbn_10 }
              user_book_reads(order_by: {id: desc}, limit: 1) { started_at finished_at }
            } } }
            """;
        var books = new List<HardcoverShelfBook>();
        for (var offset = 0; offset < 20_000; offset += 100)
        {
            if (await AskAsync(token, Query, new { offset }, cancellationToken) is not { } data || !data.TryGetProperty("me", out var me))
            {
                return null;
            }

            var account = me.ValueKind == JsonValueKind.Array ? (me.GetArrayLength() > 0 ? me[0] : default) : me;
            if (account.ValueKind != JsonValueKind.Object || !account.TryGetProperty("user_books", out var page) || page.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in page.EnumerateArray())
            {
                if (ReadShelfBook(item) is { } book)
                {
                    books.Add(book);
                }
            }

            if (page.GetArrayLength() < 100)
            {
                break;
            }
        }

        return books;
    }

    private static HardcoverShelfBook? ReadShelfBook(JsonElement item)
    {
        if (!item.TryGetProperty("id", out var id) || !id.TryGetInt32(out var userBookId) || !item.TryGetProperty("book_id", out var book) || !book.TryGetInt32(out var bookId)
            || !item.TryGetProperty("status_id", out var status) || !status.TryGetInt32(out var statusId))
        {
            return null;
        }

        var details = item.TryGetProperty("book", out var found) && found.ValueKind == JsonValueKind.Object ? found : default;
        var title = details.ValueKind == JsonValueKind.Object && details.TryGetProperty("title", out var named) ? named.GetString() ?? "" : "";
        var author = details.ValueKind == JsonValueKind.Object && details.TryGetProperty("contributions", out var people) && people.ValueKind == JsonValueKind.Array && people.GetArrayLength() > 0
            && people[0].TryGetProperty("author", out var person) && person.ValueKind == JsonValueKind.Object && person.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
        var edition = item.TryGetProperty("edition", out var copy) && copy.ValueKind == JsonValueKind.Object ? copy : default;
        string? Isbn(string field) => edition.ValueKind == JsonValueKind.Object && edition.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var read = item.TryGetProperty("user_book_reads", out var reads) && reads.ValueKind == JsonValueKind.Array && reads.GetArrayLength() > 0 ? reads[0] : default;
        DateOnly? Day(string field) => read.ValueKind == JsonValueKind.Object && read.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            && DateOnly.TryParse(value.GetString()![..Math.Min(10, value.GetString()!.Length)], CultureInfo.InvariantCulture, out var day) ? day : null;
        int? rating = item.TryGetProperty("rating", out var stars) && stars.ValueKind == JsonValueKind.Number && stars.GetDouble() > 0
            ? Math.Clamp((int)Math.Round(stars.GetDouble(), MidpointRounding.AwayFromZero), 1, 5) : null;
        return new HardcoverShelfBook(userBookId, bookId, title, author, Isbn("isbn_13") ?? Isbn("isbn_10"), new HardcoverStatus(statusId, rating, Day("started_at"), Day("finished_at")));
    }

    private async Task<JsonElement?> AskAsync(string token, string query, object? variables, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = JsonContent.Create(new { query, variables }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? token[7..] : token);
        try
        {
            var answer = await http.SendAsync(request, cancellationToken);
            if (!answer.IsSuccessStatusCode)
            {
                return null;
            }

            var document = await answer.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return document.TryGetProperty("errors", out _) || !document.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ? null : data.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool Same(string? one, string other) =>
        string.Equals(Simple(one), Simple(other), StringComparison.Ordinal);

    private static string Simple(string? text) => new((text ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}

public sealed record HardcoverSent(bool Done, int Books, int NotFound);

public sealed record HardcoverBrought(bool Done, int Changed, int Added);

// Each book's status, stars, and reading dates go to the reader's Hardcover account: those that changed since they
// last went, a few at a time, as Hardcover asks callers to take it slowly. Changes made on Hardcover come back to books
// that have not changed here since; when both changed, the shelf's own wins and goes there.
public sealed class HardcoverSync(IServiceScopeFactory scopes, IHardcoverClient client, IDataProtectionProvider protection, IConfiguration configuration, ILogger<HardcoverSync> logger) : BackgroundService
{
    public const int MostAtOnce = 40;
    private IDataProtector Protector => protection.CreateProtector("Shelf.Hardcover.Token");

    private TimeSpan Pause => TimeSpan.FromMilliseconds(configuration.GetValue("Hardcover:PauseMilliseconds", 1100));

    public async Task<string?> ConnectAsync(ShelfDb db, string? token, CancellationToken cancellationToken = default)
    {
        token = token?.Trim();
        if (string.IsNullOrEmpty(token) || token.Length > 2000 || await Family.IsRestrictedAsync(db, cancellationToken: cancellationToken) || await client.WhoAsync(token, cancellationToken) is not { } name)
        {
            return null;
        }

        var reader = await db.Readers.FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        reader.HardcoverToken = Protector.Protect(token);
        await db.SaveChangesAsync(cancellationToken);
        await Audit.NoteAsync(db, Say("Connected Hardcover"), reader, name, cancellationToken);
        return name;
    }

    public static async Task DisconnectAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var reader = await db.Readers.FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        (reader.HardcoverToken, reader.HardcoverAuto, reader.HardcoverAddMissing) = (null, false, false);
        await db.Books.Where(book => book.HardcoverState != null || book.HardcoverUserBookId != null)
            .ExecuteUpdateAsync(set => set.SetProperty(book => book.HardcoverState, (string?)null).SetProperty(book => book.HardcoverUserBookId, (int?)null), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await Audit.NoteAsync(db, Say("Disconnected Hardcover"), reader, cancellationToken: cancellationToken);
    }

    public static HardcoverStatus StatusOf(Book book) => new(
        book.Status switch
        {
            BookStatus.Want => 1,
            BookStatus.Reading => 2,
            BookStatus.Finished => 3,
            _ => 5,
        },
        book.Rating,
        book.StartedOn,
        book.FinishedOn);

    private static string State(HardcoverStatus status) => string.Join('|', status.StatusId, status.Rating,
        status.StartedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), status.FinishedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    // What was last sent, read back. Older shelves wrote the dates in the reader's own way, so any common one is read.
    private static HardcoverStatus? Sent(string? state)
    {
        var parts = state?.Split('|');
        if (parts is not { Length: 4 } || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var statusId))
        {
            return null;
        }

        static DateOnly? Day(string text)
        {
            foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("en-GB"), CultureInfo.GetCultureInfo("de-DE"), CultureInfo.GetCultureInfo("fr-FR"), CultureInfo.GetCultureInfo("es-ES") })
            {
                if (DateOnly.TryParseExact(text, "yyyy-MM-dd", culture, DateTimeStyles.None, out var exact) || DateOnly.TryParse(text, culture, out exact))
                {
                    return exact;
                }
            }

            return null;
        }

        return new HardcoverStatus(statusId, int.TryParse(parts[1], CultureInfo.InvariantCulture, out var rating) ? rating : null, Day(parts[2]), Day(parts[3]));
    }

    private static bool Unchanged(Book book) => Sent(book.HardcoverState) == StatusOf(book);

    private static BookStatus? StatusFrom(int statusId) => statusId switch
    {
        1 => BookStatus.Want,
        2 or 4 => BookStatus.Reading,
        3 => BookStatus.Finished,
        5 => BookStatus.Abandoned,
        _ => null,
    };

    // Hardcover's shelf onto this one. A book linked to one there, or found by ISBN or title and author, takes
    // Hardcover's status, stars, and dates when it has not changed here since it was last sent; one never sent takes
    // only what it lacks, and a status further along. With HardcoverAddMissing, books only on Hardcover are added.
    public async Task<HardcoverBrought> PullAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var reader = await db.Readers.AsNoTracking().FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        var token = Open(reader.HardcoverToken);
        if (token.Length == 0 || await client.ShelfAsync(token, cancellationToken) is not { } theirs)
        {
            return new HardcoverBrought(false, 0, 0);
        }

        var books = await db.Books.ToListAsync(cancellationToken);
        int changed = 0, added = 0;
        foreach (var item in theirs)
        {
            if (StatusFrom(item.Status.StatusId) is not { } status)
            {
                continue;
            }

            var isbn = BookRules.NormalizeIsbn(item.Isbn);
            var book = books.FirstOrDefault(book => book.HardcoverUserBookId == item.UserBookId)
                ?? books.FirstOrDefault(book => book.HardcoverUserBookId is null && book.HardcoverBookId == item.BookId)
                ?? books.FirstOrDefault(book => book.HardcoverUserBookId is null && isbn is not null && BookRules.NormalizeIsbn(book.Isbn) == isbn)
                ?? books.FirstOrDefault(book => book.HardcoverUserBookId is null && Same(book.Title, item.Title) && Same(book.Author, item.Author));
            if (book is null)
            {
                if (!reader.HardcoverAddMissing || item.Title.Length == 0)
                {
                    continue;
                }

                book = new Book
                {
                    Title = item.Title.Length > 200 ? item.Title[..200] : item.Title,
                    Author = item.Author.Length == 0 ? "Unknown" : item.Author.Length > 200 ? item.Author[..200] : item.Author,
                    Isbn = isbn,
                    Status = status,
                    Rating = item.Status.Rating,
                    StartedOn = item.Status.StartedOn,
                    FinishedOn = item.Status.FinishedOn,
                    AddedAt = DateTimeOffset.UtcNow,
                };
                db.Books.Add(book);
                books.Add(book);
                added++;
            }
            else
            {
                var mine = StatusOf(book);
                // A book not found there before was noted, not sent.
                var neverSent = book.HardcoverState is null || book.HardcoverUserBookId is null;
                var changedHere = !neverSent && !Unchanged(book);
                if (State(mine) != State(item.Status) && !changedHere)
                {
                    if (neverSent)
                    {
                        book.Status = Rank(status) > Rank(book.Status) ? status : book.Status;
                        book.Rating ??= item.Status.Rating;
                        book.StartedOn ??= item.Status.StartedOn;
                        book.FinishedOn ??= item.Status.FinishedOn;
                    }
                    else
                    {
                        book.Status = status;
                        book.Rating = item.Status.Rating ?? book.Rating;
                        book.StartedOn = item.Status.StartedOn ?? book.StartedOn;
                        book.FinishedOn = item.Status.FinishedOn ?? book.FinishedOn;
                    }

                    changed += State(StatusOf(book)) != State(mine) ? 1 : 0;
                }

                if (changedHere)
                {
                    // Changed on both sides: this shelf's goes there next.
                    book.HardcoverBookId = item.BookId;
                    book.HardcoverUserBookId = item.UserBookId;
                    continue;
                }
            }

            // What both now agree on counts as sent; a book that kept some of its own still has that to send.
            var sentBefore = book.HardcoverUserBookId is not null && book.HardcoverState is not null;
            book.HardcoverState = State(StatusOf(book)) == State(item.Status) || !sentBefore ? State(item.Status) : book.HardcoverState;
            (book.HardcoverBookId, book.HardcoverUserBookId) = (item.BookId, item.UserBookId);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new HardcoverBrought(true, changed, added);
    }

    private static int Rank(BookStatus status) => status switch
    {
        BookStatus.Want => 0,
        BookStatus.Reading => 1,
        _ => 2,
    };

    private static bool Same(string? one, string? other)
    {
        static string Simple(string? text) => new((text ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return Simple(one).Length > 0 && Simple(one) == Simple(other);
    }

    private string Open(string? sealedToken)
    {
        try
        {
            return sealedToken is null ? "" : Protector.Unprotect(sealedToken);
        }
        catch (CryptographicException)
        {
            return "";
        }
    }

    // The books whose status, stars, or dates changed since they last went, up to a batch of them.
    public async Task<HardcoverSent> SendAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var reader = await db.Readers.AsNoTracking().FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        string token;
        try
        {
            token = reader.HardcoverToken is null ? "" : Protector.Unprotect(reader.HardcoverToken);
        }
        catch (CryptographicException)
        {
            token = "";
        }

        if (token.Length == 0)
        {
            return new HardcoverSent(false, 0, 0);
        }

        var books = await db.Books.ToListAsync(cancellationToken);
        int sent = 0, missing = 0;
        foreach (var book in books.Where(book => !Unchanged(book)).Take(MostAtOnce))
        {
            var status = StatusOf(book);
            // The reading dates go as a new read only when they changed, so Hardcover does not count the same read twice.
            var before = Sent(book.HardcoverState);
            var forHardcover = before is not null && before.StartedOn == status.StartedOn && before.FinishedOn == status.FinishedOn ? status with { StartedOn = null, FinishedOn = null } : status;
            book.HardcoverBookId ??= await client.FindAsync(token, BookRules.NormalizeIsbn(book.Isbn), book.Title, book.Author, cancellationToken);
            if (book.HardcoverBookId is not int hardcoverId)
            {
                // Not on Hardcover by its ISBN or title: noted, so it is not looked for again until it changes.
                book.HardcoverState = State(status);
                missing++;
                continue;
            }

            if (await client.SaveAsync(token, hardcoverId, book.HardcoverUserBookId, forHardcover, cancellationToken) is int copy)
            {
                book.HardcoverUserBookId = copy;
                book.HardcoverState = State(status);
                sent++;
            }

            await db.SaveChangesAsync(cancellationToken);
            if (Pause > TimeSpan.Zero)
            {
                await Task.Delay(Pause, cancellationToken);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return new HardcoverSent(true, sent, missing);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SendDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or HttpRequestException)
            {
                logger.LogWarning(ex, "Could not send books to Hardcover; it will try again.");
            }
        }
    }

    public async Task<int> SendDueAsync(CancellationToken cancellationToken)
    {
        List<int> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            due = await scope.ServiceProvider.GetRequiredService<ShelfDb>().Readers.AsNoTracking()
                .Where(reader => reader.HardcoverAuto && reader.HardcoverToken != null).Select(reader => reader.Id).ToListAsync(cancellationToken);
        }

        var sent = 0;
        foreach (var readerId in due)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            await PullAsync(db, cancellationToken);
            sent += (await SendAsync(db, cancellationToken)).Books;
        }

        return sent;
    }
}

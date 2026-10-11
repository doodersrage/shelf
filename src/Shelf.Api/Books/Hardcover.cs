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

public interface IHardcoverClient
{
    // The account's name when Hardcover takes the token; null when it does not.
    Task<string?> WhoAsync(string token, CancellationToken cancellationToken);

    // Hardcover's id for a book, by ISBN first, then by title and author.
    Task<int?> FindAsync(string token, string? isbn, string title, string author, CancellationToken cancellationToken);

    // Puts the book on the account's shelf, or updates it there; returns Hardcover's id for the reader's copy.
    Task<int?> SaveAsync(string token, int bookId, int? userBookId, HardcoverStatus status, CancellationToken cancellationToken);
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

// Each book's status, stars, and reading dates go to the reader's Hardcover account: those that changed since they
// last went, a few at a time, as Hardcover asks callers to take it slowly. Hardcover's changes do not come back.
public sealed class HardcoverSync(IServiceScopeFactory scopes, IHardcoverClient client, IDataProtectionProvider protection, IConfiguration configuration, ILogger<HardcoverSync> logger) : BackgroundService
{
    public const int MostAtOnce = 40;
    private IDataProtector Protector => protection.CreateProtector("Shelf.Hardcover.Token");

    private TimeSpan Pause => TimeSpan.FromMilliseconds(configuration.GetValue("Hardcover:PauseMilliseconds", 1100));

    public async Task<string?> ConnectAsync(ShelfDb db, string? token, CancellationToken cancellationToken = default)
    {
        token = token?.Trim();
        if (string.IsNullOrEmpty(token) || token.Length > 2000 || await client.WhoAsync(token, cancellationToken) is not { } name)
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
        (reader.HardcoverToken, reader.HardcoverAuto) = (null, false);
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

    private static string State(HardcoverStatus status) => string.Join('|', status.StatusId, status.Rating, status.StartedOn, status.FinishedOn);

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
        foreach (var book in books.Where(book => book.HardcoverState != State(StatusOf(book))).Take(MostAtOnce))
        {
            var status = StatusOf(book);
            book.HardcoverBookId ??= await client.FindAsync(token, BookRules.NormalizeIsbn(book.Isbn), book.Title, book.Author, cancellationToken);
            if (book.HardcoverBookId is not int hardcoverId)
            {
                // Not on Hardcover by its ISBN or title: noted, so it is not looked for again until it changes.
                book.HardcoverState = State(status);
                missing++;
                continue;
            }

            if (await client.SaveAsync(token, hardcoverId, book.HardcoverUserBookId, status, cancellationToken) is int copy)
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
            sent += (await SendAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), cancellationToken)).Books;
        }

        return sent;
    }
}

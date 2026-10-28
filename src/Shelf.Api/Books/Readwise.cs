using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// One highlight as Readwise takes it.
public sealed record ReadwiseHighlight(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("author")] string Author,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("location")] int? Location,
    [property: JsonPropertyName("location_type")] string? LocationType,
    [property: JsonPropertyName("highlighted_at")] DateTimeOffset HighlightedAt,
    [property: JsonPropertyName("source_type")] string SourceType = "shelf",
    [property: JsonPropertyName("category")] string Category = "books");

// A book in the reader's Readwise account, with its highlights, as its export gives them.
public sealed record ReadwiseBook(string Title, string Author, string? Category, string? Source, IReadOnlyList<ReadwiseExported> Highlights);

public sealed record ReadwiseExported(string Text, string? Note, int? Location, string? LocationType, DateTimeOffset? HighlightedAt, bool Discarded);

public interface IReadwiseClient
{
    // Whether Readwise takes the token.
    Task<bool> CheckAsync(string token, CancellationToken cancellationToken);

    Task<bool> SendAsync(string token, IReadOnlyList<ReadwiseHighlight> highlights, CancellationToken cancellationToken);

    // Every book with highlights changed after a moment, or all of them; null when Readwise could not be read.
    Task<IReadOnlyList<ReadwiseBook>?> ExportAsync(string token, DateTimeOffset? updatedAfter, CancellationToken cancellationToken);

    // Why a trade went wrong, asked once it has: the token refused, Readwise busy or out of reach; null when it takes the token.
    Task<SyncProblem?> DiagnoseAsync(string token, CancellationToken cancellationToken);
}

public sealed class ReadwiseClient(HttpClient http) : IReadwiseClient
{
    public async Task<SyncProblem?> DiagnoseAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://readwise.io/api/v2/auth/");
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        try
        {
            return SyncProblems.FromStatus((await http.SendAsync(request, cancellationToken)).StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or Polly.ExecutionRejectedException)
        {
            return SyncProblem.Unreachable;
        }
    }

    public async Task<bool> CheckAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://readwise.io/api/v2/auth/");
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        try
        {
            return (await http.SendAsync(request, cancellationToken)).StatusCode == HttpStatusCode.NoContent;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public async Task<bool> SendAsync(string token, IReadOnlyList<ReadwiseHighlight> highlights, CancellationToken cancellationToken)
    {
        // Readwise takes them in batches; a hundred at a time keeps each request small.
        foreach (var batch in highlights.Chunk(100))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://readwise.io/api/v2/highlights/") { Content = JsonContent.Create(new { highlights = batch }) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
            try
            {
                if (!(await http.SendAsync(request, cancellationToken)).IsSuccessStatusCode)
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return false;
            }
        }

        return true;
    }

    public async Task<IReadOnlyList<ReadwiseBook>?> ExportAsync(string token, DateTimeOffset? updatedAfter, CancellationToken cancellationToken)
    {
        var books = new List<ReadwiseBook>();
        string? cursor = null;
        do
        {
            var query = new List<string>();
            if (updatedAfter is { } after)
            {
                query.Add("updatedAfter=" + Uri.EscapeDataString(after.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture)));
            }

            if (cursor is not null)
            {
                query.Add("pageCursor=" + Uri.EscapeDataString(cursor));
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://readwise.io/api/v2/export/" + (query.Count > 0 ? "?" + string.Join('&', query) : ""));
            request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
            System.Text.Json.JsonElement page;
            try
            {
                using var answer = await http.SendAsync(request, cancellationToken);
                if (!answer.IsSuccessStatusCode)
                {
                    return null;
                }

                page = await answer.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                return null;
            }

            if (page.TryGetProperty("results", out var results) && results.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                books.AddRange(results.EnumerateArray().Select(ReadBook));
            }

            cursor = page.TryGetProperty("nextPageCursor", out var next) && next.ValueKind is System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Number ? next.ToString() : null;
        }
        while (cursor is not null && books.Count < 100_000);

        return books;
    }

    private static ReadwiseBook ReadBook(System.Text.Json.JsonElement book) => new(
        Text(book, "readable_title") ?? Text(book, "title") ?? "",
        Text(book, "author") ?? "",
        Text(book, "category"),
        Text(book, "source"),
        book.TryGetProperty("highlights", out var marks) && marks.ValueKind == System.Text.Json.JsonValueKind.Array
            ? marks.EnumerateArray().Select(mark => new ReadwiseExported(
                Text(mark, "text") ?? "",
                Text(mark, "note"),
                mark.TryGetProperty("location", out var location) && location.TryGetInt32(out var at) ? at : null,
                Text(mark, "location_type"),
                Text(mark, "highlighted_at") is { } when && DateTimeOffset.TryParse(when, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var moment) ? moment
                    : Text(mark, "created_at") is { } made && DateTimeOffset.TryParse(made, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var madeAt) ? madeAt : null,
                mark.TryGetProperty("is_discard", out var discard) && discard.ValueKind == System.Text.Json.JsonValueKind.True)).ToList()
            : []);

    private static string? Text(System.Text.Json.JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text.Trim() : null;
}

public sealed record ReadwiseSent(bool Done, int Count);

public sealed record ReadwiseBrought(bool Done, KindleImport? Import);

// A reader's highlights and quotes, sent to their Readwise account with the token they gave. Readwise keeps one copy
// of each highlight however often it is sent; Shelf sends those made since the last send unless asked for all.
public sealed class ReadwiseSync(IServiceScopeFactory scopes, IReadwiseClient client, IDataProtectionProvider protection, ILogger<ReadwiseSync> logger) : BackgroundService
{
    private IDataProtector Protector => protection.CreateProtector("Shelf.Readwise.Token");

    public async Task<bool> ConnectAsync(ShelfDb db, string? token, CancellationToken cancellationToken = default)
    {
        token = token?.Trim();
        if (string.IsNullOrEmpty(token) || token.Length > 200 || await Family.IsRestrictedAsync(db, cancellationToken: cancellationToken) || !await client.CheckAsync(token, cancellationToken))
        {
            return false;
        }

        var reader = await db.Readers.FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        reader.ReadwiseToken = Protector.Protect(token);
        reader.ReadwiseSentAt = null;
        await db.SaveChangesAsync(cancellationToken);
        await Audit.NoteAsync(db, Say("Connected Readwise"), reader, cancellationToken: cancellationToken);
        return true;
    }

    public static async Task DisconnectAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var reader = await db.Readers.FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        (reader.ReadwiseToken, reader.ReadwiseAuto, reader.ReadwiseSentAt) = (null, false, null);
        (reader.ReadwiseAutoImport, reader.ReadwiseImportedAt) = (false, null);
        await db.SaveChangesAsync(cancellationToken);
        await Audit.NoteAsync(db, Say("Disconnected Readwise"), reader, cancellationToken: cancellationToken);
    }

    public async Task<ReadwiseSent> SendAsync(ShelfDb db, bool everything, CancellationToken cancellationToken = default)
    {
        var reader = await db.Readers.FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        string token;
        try
        {
            token = reader.ReadwiseToken is null ? "" : Protector.Unprotect(reader.ReadwiseToken);
        }
        catch (CryptographicException)
        {
            token = "";
        }

        if (token.Length == 0)
        {
            if (reader.ReadwiseToken is not null)
            {
                await NoteAsync(db, reader, SyncProblem.Unsealed, cancellationToken);
            }

            return new ReadwiseSent(false, 0);
        }

        var since = everything ? DateTimeOffset.MinValue : reader.ReadwiseSentAt ?? DateTimeOffset.MinValue;
        var started = DateTimeOffset.UtcNow;
        var highlights = await ItemsAsync(db, since, cancellationToken);
        if (highlights.Count > 0 && !await client.SendAsync(token, highlights, cancellationToken))
        {
            await NoteAsync(db, reader, await client.DiagnoseAsync(token, cancellationToken) ?? SyncProblem.Failed, cancellationToken);
            return new ReadwiseSent(false, 0);
        }

        reader.ReadwiseSentAt = started;
        await db.SaveChangesAsync(cancellationToken);
        await NoteAsync(db, reader, null, cancellationToken);
        return new ReadwiseSent(true, highlights.Count);
    }

    // Highlights made elsewhere (a Kindle, Apple Books, Kobo, articles) onto the books on this shelf they are from,
    // matched by title and author: those changed since the last time, or all. Shelf's own, sent there, stay out.
    public async Task<ReadwiseBrought> ImportAsync(ShelfDb db, EbookStore store, bool everything, CancellationToken cancellationToken = default)
    {
        var reader = await db.Readers.FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        var token = Open(reader.ReadwiseToken);
        if (token.Length == 0)
        {
            if (reader.ReadwiseToken is not null)
            {
                await NoteAsync(db, reader, SyncProblem.Unsealed, cancellationToken);
            }

            return new ReadwiseBrought(false, null);
        }

        var started = DateTimeOffset.UtcNow;
        var books = await client.ExportAsync(token, everything ? null : reader.ReadwiseImportedAt, cancellationToken);
        if (books is null)
        {
            await NoteAsync(db, reader, await client.DiagnoseAsync(token, cancellationToken) ?? SyncProblem.Failed, cancellationToken);
            return new ReadwiseBrought(false, null);
        }

        var clippings = books
            .Where(book => book.Category is null or "books" && !string.Equals(book.Source, "shelf", StringComparison.OrdinalIgnoreCase) && book.Title.Length > 0)
            .SelectMany(book => book.Highlights.Where(mark => !mark.Discarded && mark.Text.Length > 0).Select(mark => new KindleClippings.Clipping(
                book.Title, book.Author, "highlight", mark.Text,
                mark.LocationType == "page" && mark.Location is >= 1 and <= 20000 ? mark.Location : null,
                null, null, mark.HighlightedAt,
                mark.Note is null ? null : mark.Note[..Math.Min(mark.Note.Length, BookRules.MaxHighlightNoteLength)])));
        var imported = await KindleClippings.ImportAsync(db, store, clippings, Say("Brought highlights from Readwise"), cancellationToken);
        reader.ReadwiseImportedAt = started;
        await db.SaveChangesAsync(cancellationToken);
        await NoteAsync(db, reader, null, cancellationToken);
        return new ReadwiseBrought(true, imported);
    }

    // How the last trade went, kept on the reader for Connections and Account; written only when it changes.
    private static async Task NoteAsync(ShelfDb db, Reader reader, SyncProblem? problem, CancellationToken cancellationToken)
    {
        if (reader.ReadwiseProblem is null && problem is null)
        {
            return;
        }

        DateTimeOffset? at = problem is null ? null : DateTimeOffset.UtcNow;
        await db.Readers.Where(item => item.Id == reader.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.ReadwiseProblem, problem).SetProperty(item => item.ReadwiseProblemAt, at), cancellationToken);
        (reader.ReadwiseProblem, reader.ReadwiseProblemAt) = (problem, at);
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

    // The reader's own highlights (on their books, and on books lent to them) and quotes, made after a moment.
    private static async Task<List<ReadwiseHighlight>> ItemsAsync(ShelfDb db, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var me = db.ReaderId;
        var marks = (await db.Highlights.IgnoreQueryFilters().AsNoTracking()
            .Where(mark => (mark.ReaderId == null && mark.Book!.OwnerId == me) || mark.ReaderId == me)
            .Select(mark => new { mark.Text, mark.Note, mark.ChapterIndex, mark.NotedAt, mark.Book!.Title, mark.Book.Author })
            .ToListAsync(cancellationToken))
            .Where(mark => mark.NotedAt > since)
            .Select(mark => new ReadwiseHighlight(mark.Text, mark.Title, mark.Author, mark.Note, mark.ChapterIndex + 1, "order", mark.NotedAt));
        var quotes = (await db.Quotes.AsNoTracking()
            .Select(quote => new { quote.Text, quote.Page, quote.NotedAt, quote.Book!.Title, quote.Book.Author })
            .ToListAsync(cancellationToken))
            .Where(quote => quote.NotedAt > since)
            .Select(quote => new ReadwiseHighlight(quote.Text, quote.Title, quote.Author, null, quote.Page, quote.Page is null ? null : "page", quote.NotedAt));
        return marks.Concat(quotes).Where(item => item.Text.Length is > 0 and <= 8191).ToList();
    }

    // Every hour, new highlights go to Readwise for readers who asked.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SendDueAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not send highlights to Readwise; it will try again.");
            }
        }
    }

    // A round that went wrong in a way nothing expected is still told to the reader, from a fresh start.
    private async Task NoteFailedAsync(int readerId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var now = DateTimeOffset.UtcNow;
            await db.Readers.Where(item => item.Id == readerId).ExecuteUpdateAsync(set => set.SetProperty(item => item.ReadwiseProblem, SyncProblem.Failed).SetProperty(item => item.ReadwiseProblemAt, now), cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not note the Readwise trouble for reader {ReaderId}.", readerId);
        }
    }

    public async Task<int> SendDueAsync(CancellationToken cancellationToken)
    {
        List<int> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            due = await scope.ServiceProvider.GetRequiredService<ShelfDb>().Readers.AsNoTracking()
                .Where(reader => (reader.ReadwiseAuto || reader.ReadwiseAutoImport) && reader.ReadwiseToken != null).Select(reader => reader.Id).ToListAsync(cancellationToken);
        }

        var sent = 0;
        foreach (var readerId in due)
        {
            // One reader's trouble (a revoked token, say) is theirs alone: the readers after them still go.
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
                var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
                var reader = await db.Readers.AsNoTracking().FirstAsync(item => item.Id == readerId, cancellationToken);
                if (reader.ReadwiseAutoImport)
                {
                    await ImportAsync(db, scope.ServiceProvider.GetRequiredService<EbookStore>(), everything: false, cancellationToken);
                }

                if (reader.ReadwiseAuto)
                {
                    sent += (await SendAsync(db, everything: false, cancellationToken)).Count;
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not keep reader {ReaderId}'s highlights in step with Readwise; the others go on.", readerId);
                await NoteFailedAsync(readerId, cancellationToken);
            }
        }

        return sent;
    }
}

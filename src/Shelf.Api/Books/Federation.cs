using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// Another Shelf this one is linked to, so the readers of each can see the other's open shelves and borrow from them.
// Each side holds the secret it sends when it calls the other (sealed), and a hash of the one the other sends it.
public sealed class ShelfLink
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string Address { get; set; } = "";
    public string? TheirSecret { get; set; }
    public string? OurSecretHash { get; set; }

    // While waiting for the other shelf to take the invitation: a hash of its one-time code, and when it runs out.
    public string? InviteHash { get; set; }
    public DateTimeOffset? InviteExpires { get; set; }
    public bool Active { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
}

// A reader on a linked shelf, as this one knows them: by that shelf's id for them and the name it gives.
public sealed class RemoteReader
{
    public int Id { get; set; }
    public int LinkId { get; set; }
    public int RemoteId { get; set; }
    public required string Name { get; set; }
}

// An ask to borrow one of this shelf's books, from a reader on a linked shelf.
public sealed class RemoteAsk
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public int RemoteReaderId { get; set; }
    public RemoteReader? Reader { get; set; }
    public DateTimeOffset AskedAt { get; set; }
}

// An ask this shelf's reader made of a book on a linked shelf, kept until it is lent or declined.
public sealed class LinkedAsk
{
    public int Id { get; set; }
    public int LinkId { get; set; }
    public int ReaderId { get; set; }
    public int RemoteBookId { get; set; }
    public required string Title { get; set; }
    public required string Author { get; set; }
    public required string Owner { get; set; }
    public DateTimeOffset AskedAt { get; set; }
}

public sealed record LinkAccept(string Token, string Address, string Name, string Secret);

public sealed record LinkAccepted(string Name, string Secret);

public sealed record LinkHello(string Name);

public sealed record LinkedShelf(int LinkId, string Server, int OwnerId, string Name, int Books);

public sealed record LinkedBook(int Id, string Title, string Author, string? CoverUrl, int? Year, bool OnLoan, bool HasEbook, bool HasAudio, bool Asked);

public sealed record LinkAskRequest(int BookId, int ReaderId, string ReaderName);

public sealed record LinkedLoan(int BookId, string Title, string Author, string? Subtitle, int? Year, string? Isbn, string? CoverUrl, string Owner, DateOnly? DueOn, bool HasEbook, bool HasAudio, string? EbookName, string? AudioName);

public sealed record ShelfLinkSummary(int Id, string Name, string Address, bool Active, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt);

public sealed record RemoteAskRow(int Id, int BookId, string Title, string Author, string Reader, string Server, DateTimeOffset AskedAt, bool OnLoan);

public sealed record LinkedAskRow(int Id, string Title, string Author, string Owner, DateTimeOffset AskedAt);

// Calls to linked shelves, each with the secret that shelf gave this one.
public sealed class LinkClient(IHttpClientFactory clients, IDataProtectionProvider protection)
{
    public const string ClientName = "shelf-link";

    private IDataProtector Protector => protection.CreateProtector("Shelf.Links.Secret");

    public string Seal(string secret) => Protector.Protect(secret);

    private HttpClient Client => clients.CreateClient(ClientName);

    private HttpRequestMessage Request(ShelfLink link, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, link.Address.TrimEnd('/') + path);
        try
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", link.TheirSecret is null ? "" : Protector.Unprotect(link.TheirSecret));
        }
        catch (CryptographicException)
        {
            // Keys gone: the call is refused, and the link needs making again.
        }

        return request;
    }

    public async Task<T?> GetAsync<T>(ShelfLink link, string path, CancellationToken cancellationToken)
    {
        try
        {
            using var request = Request(link, HttpMethod.Get, path);
            using var answer = await Client.SendAsync(request, cancellationToken);
            return answer.IsSuccessStatusCode ? await answer.Content.ReadFromJsonAsync<T>(cancellationToken) : default;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return default;
        }
    }

    public async Task<bool> SendAsync(ShelfLink link, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        try
        {
            using var request = Request(link, method, path);
            request.Content = body is null ? null : JsonContent.Create(body);
            using var answer = await Client.SendAsync(request, cancellationToken);
            return answer.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    // A file to a temporary place; null when it could not be had.
    public async Task<string?> DownloadAsync(ShelfLink link, string path, long mostBytes, CancellationToken cancellationToken)
    {
        var target = Path.Combine(Path.GetTempPath(), $"shelf-link-{Guid.NewGuid():N}");
        var whole = false;
        try
        {
            using var request = Request(link, HttpMethod.Get, path);
            using var answer = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!answer.IsSuccessStatusCode || answer.Content.Headers.ContentLength > mostBytes)
            {
                return null;
            }

            await using var source = await answer.Content.ReadAsStreamAsync(cancellationToken);
            await using var file = File.Create(target);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if ((total += read) > mostBytes)
                {
                    return null;
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            whole = true;
            return target;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
        finally
        {
            // Anything short of the whole file (too big, cut off, cancelled) is no use to anyone: it goes.
            if (!whole && File.Exists(target))
            {
                File.Delete(target);
            }
        }
    }

    public async Task<LinkAccepted?> AcceptAsync(string address, LinkAccept accept, CancellationToken cancellationToken)
    {
        try
        {
            using var answer = await Client.PostAsJsonAsync(address.TrimEnd('/') + "/federation/accept", accept, cancellationToken);
            return answer.IsSuccessStatusCode ? await answer.Content.ReadFromJsonAsync<LinkAccepted>(cancellationToken) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return null;
        }
    }
}

// Linked shelves: what this shelf offers others (its readers' open shelves, and the books it lends them), and what
// its readers borrow from theirs. A book lent to a reader on another shelf goes there as a copy while it is out; when
// it comes back, the copy's files go and the borrower's notes stay.
public sealed class Federation(IServiceScopeFactory scopes, LinkClient client, EbookStore ebooks, AudioStore audio, CoverStore covers, Notices notices, IConfiguration configuration, ILogger<Federation> logger) : BackgroundService
{
    public const int MostLinks = 20;
    public const string SecretPrefix = "link_";

    public string Name => configuration["Federation:Name"] is { Length: > 0 } name ? name : T("Shelf at {0}", Host);

    private string Host => Uri.TryCreate(EmailRules.PublicAddress(configuration, null), UriKind.Absolute, out var site) ? site.Host : "home";

    public static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string NewSecret() => SecretPrefix + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    // ---- Links, made and kept by admins ----

    public static async Task<ShelfLinkSummary[]> ListAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        (await db.ShelfLinks.AsNoTracking().ToListAsync(cancellationToken)).OrderBy(link => link.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(link => new ShelfLinkSummary(link.Id, link.Name, link.Address, link.Active, link.CreatedAt, link.LastSeenAt)).ToArray();

    // A code for the other shelf's admin, good for a day and once: this shelf's address and a one-time secret.
    public async Task<(string? Code, string? Problem)> InviteAsync(ShelfDb db, string publicAddress, CancellationToken cancellationToken = default)
    {
        if (!Notices.IsWebAddress(publicAddress, out _))
        {
            return (null, T("This shelf needs its own address set (Email:PublicAddress) before another shelf can reach it."));
        }

        if (await db.ShelfLinks.CountAsync(cancellationToken) >= MostLinks)
        {
            return (null, T("This shelf has {0} links, the most there can be.", MostLinks));
        }

        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
        db.ShelfLinks.Add(new ShelfLink { Name = T("Waiting for the other shelf"), InviteHash = Hash(token), InviteExpires = DateTimeOffset.UtcNow.AddDays(1), CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
        var code = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { a = publicAddress.TrimEnd('/'), t = token }));
        return ("shelf:" + code, null);
    }

    // The other shelf's code: this shelf calls it with a secret of its own, and is given one to call with.
    public async Task<(ShelfLinkSummary? Link, string? Problem)> JoinAsync(ShelfDb db, string? code, string publicAddress, CancellationToken cancellationToken = default)
    {
        string address, token;
        try
        {
            using var json = JsonDocument.Parse(WebEncoders.Base64UrlDecode((code ?? "").Trim().Replace("shelf:", "", StringComparison.Ordinal)));
            (address, token) = (json.RootElement.GetProperty("a").GetString()!, json.RootElement.GetProperty("t").GetString()!);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentNullException)
        {
            return (null, T("That is not a link code from another shelf."));
        }

        if (!Notices.IsWebAddress(address, out var other) || !Notices.IsWebAddress(publicAddress, out _))
        {
            return (null, !Notices.IsWebAddress(publicAddress, out _) ? T("This shelf needs its own address set (Email:PublicAddress) before another shelf can reach it.") : T("That is not a link code from another shelf."));
        }

        if (await db.ShelfLinks.CountAsync(cancellationToken) >= MostLinks)
        {
            return (null, T("This shelf has {0} links, the most there can be.", MostLinks));
        }

        var ours = NewSecret();
        var accepted = await client.AcceptAsync(other.AbsoluteUri, new LinkAccept(token, publicAddress.TrimEnd('/'), Name, ours), cancellationToken);
        if (accepted is null || !accepted.Secret.StartsWith(SecretPrefix, StringComparison.Ordinal))
        {
            return (null, T("The other shelf did not take the code. It may have been used, or run out; ask for a new one."));
        }

        var link = new ShelfLink
        {
            Name = Fit(accepted.Name, 120),
            Address = other.AbsoluteUri.TrimEnd('/'),
            TheirSecret = client.Seal(accepted.Secret),
            OurSecretHash = Hash(ours),
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.ShelfLinks.Add(link);
        await db.SaveChangesAsync(cancellationToken);
        await Audit.NoteAsync(db, Say("Linked another shelf"), detail: link.Name, cancellationToken: cancellationToken);
        return (new ShelfLinkSummary(link.Id, link.Name, link.Address, true, link.CreatedAt, null), null);
    }

    private async Task<Results<Ok<LinkAccepted>, UnauthorizedHttpResult, ValidationProblem>> Accept(LinkAccept accept, ShelfDb db, CancellationToken cancellationToken)
    {
        var hash = Hash(accept.Token ?? "");
        var link = await db.ShelfLinks.FirstOrDefaultAsync(item => item.InviteHash == hash && !item.Active, cancellationToken);
        if (link is null || link.InviteExpires < DateTimeOffset.UtcNow)
        {
            return TypedResults.Unauthorized();
        }

        if (!Notices.IsWebAddress(accept.Address, out var address) || accept.Secret?.StartsWith(SecretPrefix, StringComparison.Ordinal) != true || accept.Secret.Length > 200)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["address"] = ["An address and a secret are needed."] });
        }

        var ours = NewSecret();
        (link.Name, link.Address, link.TheirSecret, link.OurSecretHash, link.InviteHash, link.InviteExpires, link.Active) =
            (Fit(string.IsNullOrWhiteSpace(accept.Name) ? address.Host : accept.Name, 120), address.AbsoluteUri.TrimEnd('/'), client.Seal(accept.Secret), Hash(ours), null, null, true);
        await db.SaveChangesAsync(cancellationToken);
        await Audit.NoteAsync(db, Say("Linked another shelf"), detail: link.Name, cancellationToken: cancellationToken);
        return TypedResults.Ok(new LinkAccepted(Name, ours));
    }

    public async Task RemoveAsync(ShelfDb db, int id, CancellationToken cancellationToken = default)
    {
        // Copies lent from there go back now, as they can no longer be returned.
        foreach (var book in await db.Books.IgnoreQueryFilters().Where(book => book.VisitingLinkId == id).ToListAsync(cancellationToken))
        {
            Return(book);
        }

        await db.Books.IgnoreQueryFilters().Where(book => book.RemoteBorrower!.LinkId == id).ExecuteUpdateAsync(set => set.SetProperty(book => book.RemoteBorrowerId, (int?)null), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await db.ShelfLinks.Where(link => link.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    // ---- What this shelf offers linked ones ----

    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/federation").AllowAnonymous().WithTags("Linked shelves").DisableAntiforgery();
        group.MapPost("/accept", Accept);
        var linked = group.MapGroup("").AddEndpointFilter(Linked);
        linked.MapGet("/hello", () => TypedResults.Ok(new LinkHello(Name)));
        linked.MapGet("/shelves", OpenShelves);
        linked.MapGet("/shelves/{ownerId:int}", OpenShelf);
        linked.MapGet("/shelves/{ownerId:int}/books", OpenShelf);
        linked.MapPost("/asks", Ask);
        linked.MapGet("/asks", Asked);
        linked.MapDelete("/asks/{bookId:int}", Unask);
        linked.MapGet("/loans", Loans);
        linked.MapGet("/loans/{bookId:int}/ebook", LoanEbook);
        linked.MapGet("/loans/{bookId:int}/audio", LoanAudio);
        linked.MapPost("/loans/{bookId:int}/return", ReturnLoan);
    }

    // Every call from a linked shelf carries the secret this shelf gave it.
    private static async ValueTask<object?> Linked(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var header = http.Request.Headers.Authorization.ToString();
        var secret = header.StartsWith("Bearer " + SecretPrefix, StringComparison.Ordinal) ? header[7..] : null;
        var db = http.RequestServices.GetRequiredService<ShelfDb>();
        var hash = secret is { Length: < 200 } ? Hash(secret) : null;
        var link = hash is null ? null : await db.ShelfLinks.FirstOrDefaultAsync(item => item.Active && item.OurSecretHash == hash, http.RequestAborted);
        if (link is null)
        {
            return TypedResults.Unauthorized();
        }

        if (link.LastSeenAt is null || link.LastSeenAt < DateTimeOffset.UtcNow.AddMinutes(-5))
        {
            link.LastSeenAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(http.RequestAborted);
        }

        http.Items["shelf-link"] = link;
        return await next(context);
    }

    private static ShelfLink LinkOf(HttpContext http) => (ShelfLink)http.Items["shelf-link"]!;

    private static async Task<Ok<LinkedShelf[]>> OpenShelves(ShelfDb db, HttpContext http, CancellationToken cancellationToken)
    {
        var shelves = await db.Readers.AsNoTracking().Where(reader => reader.ShelfOpen && !reader.Restricted)
            .Select(reader => new { reader.Id, reader.Name, Books = db.Books.IgnoreQueryFilters().Count(book => book.OwnerId == reader.Id && book.VisitingLinkId == null) })
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(shelves.Where(shelf => shelf.Books > 0).OrderBy(shelf => shelf.Name, StringComparer.OrdinalIgnoreCase)
            .Select(shelf => new LinkedShelf(0, "", shelf.Id, shelf.Name, shelf.Books)).ToArray());
    }

    // The books of an open shelf, by title: all of them, as shelves before 2.16.2 ask, or, at /books, those matching q,
    // or the one bookId, take at a time from skip.
    private static async Task<Results<Ok<LinkedBook[]>, NotFound>> OpenShelf(
        int ownerId,
        int? readerId,
        string? q,
        int? bookId,
        int? skip,
        int? take,
        ShelfDb db,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!await db.Readers.AnyAsync(reader => reader.Id == ownerId && reader.ShelfOpen && !reader.Restricted, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var link = LinkOf(http);
        var asked = readerId is int asker
            ? (await db.RemoteAsks.AsNoTracking().Where(ask => ask.Reader!.LinkId == link.Id && ask.Reader.RemoteId == asker).Select(ask => ask.BookId).ToListAsync(cancellationToken)).ToHashSet()
            : [];
        var query = db.Books.IgnoreQueryFilters().AsNoTracking().Where(book => book.OwnerId == ownerId && book.VisitingLinkId == null);
        if (bookId is int id)
        {
            query = query.Where(book => book.Id == id);
        }

        foreach (var term in Terms(q))
        {
            query = query.Where(book => book.Title.ToLower().Contains(term) || book.Author.ToLower().Contains(term));
        }

        var ordered = query.OrderBy(book => book.Title.ToLower()).ThenBy(book => book.Id).Skip(Math.Max(0, skip ?? 0));
        if (take is int most)
        {
            ordered = ordered.Take(Math.Clamp(most, 0, MaxShelfPage));
        }

        var books = await ordered
            .Select(book => new { book.Id, book.Title, book.Author, book.CoverUrl, book.Year, book.LoanedTo, book.EbookStoredName, book.AudioStoredName })
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(books
            .Select(book => new LinkedBook(book.Id, book.Title, book.Author, book.CoverUrl is { } cover && cover.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? cover : null,
                book.Year, book.LoanedTo is not null, book.EbookStoredName is not null, book.AudioStoredName is not null, asked.Contains(book.Id)))
            .ToArray());
    }

    private const int MaxShelfPage = 200;

    private static string[] Terms(string? search) =>
        (search ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private async Task<RemoteReader> ReaderAsync(ShelfDb db, ShelfLink link, int remoteId, string? name, CancellationToken cancellationToken)
    {
        var reader = await db.RemoteReaders.FirstOrDefaultAsync(item => item.LinkId == link.Id && item.RemoteId == remoteId, cancellationToken);
        if (reader is null)
        {
            reader = new RemoteReader { LinkId = link.Id, RemoteId = remoteId, Name = Fit(string.IsNullOrWhiteSpace(name) ? "?" : name, 120) };
            db.RemoteReaders.Add(reader);
        }
        else if (!string.IsNullOrWhiteSpace(name))
        {
            reader.Name = Fit(name, 120);
        }

        await db.SaveChangesAsync(cancellationToken);
        return reader;
    }

    private async Task<Results<NoContent, NotFound>> Ask(LinkAskRequest request, ShelfDb db, HttpContext http, CancellationToken cancellationToken)
    {
        var link = LinkOf(http);
        var book = await db.Books.IgnoreQueryFilters().AsNoTracking()
            .Where(item => item.Id == request.BookId && item.VisitingLinkId == null && item.Owner != null && item.Owner.ShelfOpen && !item.Owner.Restricted)
            .Select(item => new { item.Id, item.Title, item.OwnerId }).FirstOrDefaultAsync(cancellationToken);
        if (book is null)
        {
            return TypedResults.NotFound();
        }

        var reader = await ReaderAsync(db, link, request.ReaderId, request.ReaderName, cancellationToken);
        if (!await db.RemoteAsks.AnyAsync(ask => ask.BookId == book.Id && ask.RemoteReaderId == reader.Id, cancellationToken))
        {
            db.RemoteAsks.Add(new RemoteAsk { BookId = book.Id, RemoteReaderId = reader.Id, AskedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(cancellationToken);
            var (who, server, title) = (reader.Name, link.Name, book.Title);
            await notices.SendAsync(book.OwnerId!.Value, NoticeKinds.Asks, _ => new Notice(T("{0} on {1} asks to borrow a book", who, server), title, "/loans", $"ask-{book.Id}"), cancellationToken);
        }

        return TypedResults.NoContent();
    }

    private static async Task<Ok<int[]>> Asked(int readerId, ShelfDb db, HttpContext http, CancellationToken cancellationToken)
    {
        var link = LinkOf(http);
        return TypedResults.Ok(await db.RemoteAsks.AsNoTracking().Where(ask => ask.Reader!.LinkId == link.Id && ask.Reader.RemoteId == readerId).Select(ask => ask.BookId).ToArrayAsync(cancellationToken));
    }

    private static async Task<NoContent> Unask(int bookId, int readerId, ShelfDb db, HttpContext http, CancellationToken cancellationToken)
    {
        var link = LinkOf(http);
        await db.RemoteAsks.Where(ask => ask.BookId == bookId && ask.Reader!.LinkId == link.Id && ask.Reader.RemoteId == readerId).ExecuteDeleteAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static IQueryable<Book> LentTo(ShelfDb db, ShelfLink link, int readerId) =>
        db.Books.IgnoreQueryFilters().Where(book => book.LoanedTo != null && book.RemoteBorrower != null && book.RemoteBorrower.LinkId == link.Id && book.RemoteBorrower.RemoteId == readerId);

    private static async Task<Ok<LinkedLoan[]>> Loans(int readerId, ShelfDb db, HttpContext http, CancellationToken cancellationToken)
    {
        var link = LinkOf(http);
        var books = await LentTo(db, link, readerId).AsNoTracking().Include(book => book.Owner).ToListAsync(cancellationToken);
        return TypedResults.Ok(books.Select(book => new LinkedLoan(book.Id, book.Title, book.Author, book.Subtitle, book.Year, book.Isbn,
            book.CoverUrl is { } cover && cover.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? cover : null, book.Owner?.Name ?? "", book.DueOn,
            book.EbookStoredName is not null, book.AudioStoredName is not null, book.EbookFileName, book.AudioFileName)).ToArray());
    }

    private async Task<IResult> LoanEbook(int bookId, int readerId, ShelfDb db, HttpContext http, CancellationToken cancellationToken)
    {
        var book = await LentTo(db, LinkOf(http), readerId).AsNoTracking().FirstOrDefaultAsync(item => item.Id == bookId, cancellationToken);
        return book is not null && ebooks.OpenPath(book.EbookStoredName) is { } path
            ? Results.File(path, "application/octet-stream", book.EbookFileName ?? Path.GetFileName(path))
            : Results.NotFound();
    }

    private async Task<IResult> LoanAudio(int bookId, int readerId, ShelfDb db, HttpContext http, CancellationToken cancellationToken)
    {
        var book = await LentTo(db, LinkOf(http), readerId).AsNoTracking().FirstOrDefaultAsync(item => item.Id == bookId, cancellationToken);
        if (book is null || audio.Tracks(book.AudioStoredName).Count == 0)
        {
            return Results.NotFound();
        }

        http.Response.ContentType = "application/zip";
        await audio.WriteZipAsync(book.AudioStoredName, http.Response.Body, cancellationToken);
        return Results.Empty;
    }

    private async Task<NoContent> ReturnLoan(int bookId, int readerId, ShelfDb db, HttpContext http, CancellationToken cancellationToken)
    {
        var link = LinkOf(http);
        var book = await LentTo(db, link, readerId).FirstOrDefaultAsync(item => item.Id == bookId, cancellationToken);
        if (book is not null)
        {
            BookRules.ReturnLoan(book);
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.NoContent();
    }

    // ---- The owner's side of asks from linked shelves ----

    public static async Task<RemoteAskRow[]> RemoteAsksAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var rows = await (
                from ask in db.RemoteAsks.AsNoTracking()
                join book in db.Books on ask.BookId equals book.Id
                join link in db.ShelfLinks on ask.Reader!.LinkId equals link.Id
                select new { ask.Id, ask.BookId, book.Title, book.Author, Reader = ask.Reader!.Name, Server = link.Name, ask.AskedAt, OnLoan = book.LoanedTo != null })
            .ToListAsync(cancellationToken);
        return rows.OrderBy(row => row.AskedAt).Select(row => new RemoteAskRow(row.Id, row.BookId, row.Title, row.Author, row.Reader, row.Server, row.AskedAt, row.OnLoan)).ToArray();
    }

    public static async Task<LendResult> LendRemoteAskAsync(ShelfDb db, int askId, DateOnly? dueOn, CancellationToken cancellationToken = default)
    {
        var ask = await db.RemoteAsks.Include(item => item.Reader).FirstOrDefaultAsync(item => item.Id == askId, cancellationToken);
        var book = ask is null ? null : await db.Books.FirstOrDefaultAsync(item => item.Id == ask.BookId, cancellationToken);
        if (ask is null || book is null)
        {
            return LendResult.NoBook;
        }

        if (book.LoanedTo is not null)
        {
            return LendResult.AlreadyOut;
        }

        var server = await db.ShelfLinks.AsNoTracking().Where(link => link.Id == ask.Reader!.LinkId).Select(link => link.Name).FirstOrDefaultAsync(cancellationToken) ?? "";
        (book.RemoteBorrowerId, book.LoanedTo, book.LoanedOn, book.DueOn) = (ask.RemoteReaderId, Fit($"{ask.Reader!.Name} ({server})", 120), db.Today, dueOn);
        db.RemoteAsks.Remove(ask);
        await db.SaveChangesAsync(cancellationToken);
        return LendResult.Lent;
    }

    public static async Task DropRemoteAskAsync(ShelfDb db, int askId, CancellationToken cancellationToken = default)
    {
        var ask = await db.RemoteAsks.FirstOrDefaultAsync(item => item.Id == askId, cancellationToken);
        if (ask is not null && await db.Books.AnyAsync(book => book.Id == ask.BookId, cancellationToken))
        {
            db.RemoteAsks.Remove(ask);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    // ---- The borrower's side: linked shelves, asks, and copies lent from there ----

    // Every open shelf on every linked shelf, asked now; a shelf that does not answer is left out.
    public async Task<LinkedShelf[]> ShelvesAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        if (await Family.IsRestrictedAsync(db, cancellationToken: cancellationToken))
        {
            return [];
        }

        var found = new List<LinkedShelf>();
        foreach (var link in await db.ShelfLinks.AsNoTracking().Where(link => link.Active).ToListAsync(cancellationToken))
        {
            found.AddRange((await client.GetAsync<LinkedShelf[]>(link, "/federation/shelves", cancellationToken) ?? []).Select(shelf => shelf with { LinkId = link.Id, Server = link.Name }));
        }

        return [.. found];
    }

    // A page of a reader's open shelf on a linked shelf, and whether there are more. A linked shelf from before
    // 2.16.2 sends every book whatever is asked, so the page is cut from that here.
    public async Task<(string Server, string Owner, LinkedBook[] Books, bool More)?> ShelfAsync(
        ShelfDb db, int linkId, int ownerId, string? search = null, int skip = 0, int take = 60, CancellationToken cancellationToken = default)
    {
        if (await Family.IsRestrictedAsync(db, cancellationToken: cancellationToken) || await db.ShelfLinks.AsNoTracking().FirstOrDefaultAsync(link => link.Id == linkId && link.Active, cancellationToken) is not { } link)
        {
            return null;
        }

        var shelves = await client.GetAsync<LinkedShelf[]>(link, "/federation/shelves", cancellationToken);
        var path = $"/federation/shelves/{ownerId}/books?readerId={db.ReaderId}&skip={skip}&take={take + 1}"
            + (string.IsNullOrWhiteSpace(search) ? "" : $"&q={Uri.EscapeDataString(search)}");
        var books = await client.GetAsync<LinkedBook[]>(link, path, cancellationToken);
        if (books is null)
        {
            // A shelf before 2.16.2 has no /books, and sends every book.
            var terms = Terms(search);
            books = (await client.GetAsync<LinkedBook[]>(link, $"/federation/shelves/{ownerId}?readerId={db.ReaderId}", cancellationToken))?
                .Where(book => terms.All(term => book.Title.Contains(term, StringComparison.OrdinalIgnoreCase) || book.Author.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(book => book.Title, StringComparer.OrdinalIgnoreCase).ThenBy(book => book.Id)
                .Skip(skip).Take(take + 1)
                .ToArray();
        }

        return books is null ? null : (link.Name, shelves?.FirstOrDefault(shelf => shelf.OwnerId == ownerId)?.Name ?? "", [.. books.Take(take)], books.Length > take);
    }

    // One book of a reader's open shelf on a linked shelf; from a shelf before 2.16.2, found among them all.
    private async Task<(string Server, string Owner, LinkedBook Book)?> LinkedBookAsync(ShelfDb db, ShelfLink link, int ownerId, int bookId, CancellationToken cancellationToken)
    {
        var shelves = await client.GetAsync<LinkedShelf[]>(link, "/federation/shelves", cancellationToken);
        var books = await client.GetAsync<LinkedBook[]>(link, $"/federation/shelves/{ownerId}/books?readerId={db.ReaderId}&bookId={bookId}&take=1", cancellationToken)
            ?? await client.GetAsync<LinkedBook[]>(link, $"/federation/shelves/{ownerId}?readerId={db.ReaderId}", cancellationToken);
        return books?.FirstOrDefault(book => book.Id == bookId) is { } book
            ? (link.Name, shelves?.FirstOrDefault(shelf => shelf.OwnerId == ownerId)?.Name ?? "", book)
            : null;
    }

    public async Task<bool> AskAsync(ShelfDb db, int linkId, int ownerId, int bookId, CancellationToken cancellationToken = default)
    {
        var me = await db.Readers.AsNoTracking().FirstOrDefaultAsync(reader => reader.Id == db.ReaderId, cancellationToken);
        var link = await db.ShelfLinks.AsNoTracking().FirstOrDefaultAsync(item => item.Id == linkId && item.Active, cancellationToken);
        if (me is null || me.Restricted || link is null || await LinkedBookAsync(db, link, ownerId, bookId, cancellationToken) is not { } shelf || shelf.Book is not { } book)
        {
            return false;
        }

        if (!await client.SendAsync(link, HttpMethod.Post, "/federation/asks", new LinkAskRequest(bookId, me.Id, me.Name), cancellationToken))
        {
            return false;
        }

        if (!await db.LinkedAsks.AnyAsync(ask => ask.LinkId == linkId && ask.ReaderId == me.Id && ask.RemoteBookId == bookId, cancellationToken))
        {
            db.LinkedAsks.Add(new LinkedAsk { LinkId = linkId, ReaderId = me.Id, RemoteBookId = bookId, Title = book.Title, Author = book.Author, Owner = $"{shelf.Owner} ({shelf.Server})", AskedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    public static async Task<LinkedAskRow[]> LinkedAsksAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        (await db.LinkedAsks.AsNoTracking().Where(ask => ask.ReaderId == db.ReaderId).ToListAsync(cancellationToken))
            .OrderBy(ask => ask.AskedAt).Select(ask => new LinkedAskRow(ask.Id, ask.Title, ask.Author, ask.Owner, ask.AskedAt)).ToArray();

    public async Task WithdrawAsync(ShelfDb db, int askId, CancellationToken cancellationToken = default)
    {
        var ask = await db.LinkedAsks.FirstOrDefaultAsync(item => item.Id == askId && item.ReaderId == db.ReaderId, cancellationToken);
        if (ask is null)
        {
            return;
        }

        if (await db.ShelfLinks.AsNoTracking().FirstOrDefaultAsync(link => link.Id == ask.LinkId, cancellationToken) is { } link)
        {
            await client.SendAsync(link, HttpMethod.Delete, $"/federation/asks/{ask.RemoteBookId}?readerId={ask.ReaderId}", null, cancellationToken);
        }

        db.LinkedAsks.Remove(ask);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> GiveBackAsync(ShelfDb db, int bookId, CancellationToken cancellationToken = default)
    {
        var book = await db.Books.FirstOrDefaultAsync(item => item.Id == bookId && item.VisitingLinkId != null, cancellationToken);
        var link = book is null ? null : await db.ShelfLinks.AsNoTracking().FirstOrDefaultAsync(item => item.Id == book.VisitingLinkId, cancellationToken);
        if (book is null || link is null || !await client.SendAsync(link, HttpMethod.Post, $"/federation/loans/{book.VisitingBookId}/return?readerId={db.ReaderId}", null, cancellationToken))
        {
            return false;
        }

        Return(book);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Brings this reader's loans from each linked shelf up to date: new ones arrive as copies, returned ones go.
    public async Task SyncAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var me = db.ReaderId;
        if (me == 0)
        {
            return;
        }

        var asks = await db.LinkedAsks.Where(ask => ask.ReaderId == me).ToListAsync(cancellationToken);
        var visiting = await db.Books.Where(book => book.VisitingLinkId != null).ToListAsync(cancellationToken);
        foreach (var linkId in asks.Select(ask => ask.LinkId).Concat(visiting.Select(book => book.VisitingLinkId!.Value)).Distinct())
        {
            if (await db.ShelfLinks.AsNoTracking().FirstOrDefaultAsync(link => link.Id == linkId && link.Active, cancellationToken) is not { } link
                || await client.GetAsync<LinkedLoan[]>(link, $"/federation/loans?readerId={me}", cancellationToken) is not { } loans)
            {
                continue;
            }

            foreach (var loan in loans)
            {
                var copy = visiting.FirstOrDefault(book => book.VisitingLinkId == linkId && book.VisitingBookId == loan.BookId);
                if (copy is null)
                {
                    copy = await ArriveAsync(db, link, loan, cancellationToken);
                    if (copy is null)
                    {
                        continue;
                    }

                    visiting.Add(copy);
                }

                copy.VisitingDueOn = loan.DueOn;
                db.LinkedAsks.RemoveRange(asks.Where(ask => ask.LinkId == linkId && ask.RemoteBookId == loan.BookId));
            }

            foreach (var gone in visiting.Where(book => book.VisitingLinkId == linkId && loans.All(loan => loan.BookId != book.VisitingBookId)).ToList())
            {
                Return(gone);
            }

            // Asks the owner declined are no longer there to wait on.
            if (await client.GetAsync<int[]>(link, $"/federation/asks?readerId={me}", cancellationToken) is { } waiting)
            {
                db.LinkedAsks.RemoveRange(asks.Where(ask => ask.LinkId == linkId && !waiting.Contains(ask.RemoteBookId) && loans.All(loan => loan.BookId != ask.RemoteBookId)));
            }

            await db.SaveChangesAsync(cancellationToken);
        }
    }

    // A book lent from a linked shelf: a copy on this reader's shelf, with its files, marked whose it is.
    private async Task<Book?> ArriveAsync(ShelfDb db, ShelfLink link, LinkedLoan loan, CancellationToken cancellationToken)
    {
        var book = new Book
        {
            Title = Fit(loan.Title, 200),
            Author = Fit(loan.Author, 200),
            Subtitle = loan.Subtitle,
            Year = loan.Year,
            Isbn = loan.Isbn,
            CoverUrl = loan.CoverUrl,
            Status = BookStatus.Reading,
            AddedAt = DateTimeOffset.UtcNow,
            VisitingLinkId = link.Id,
            VisitingBookId = loan.BookId,
            VisitingOwner = Fit($"{loan.Owner} ({link.Name})", 200),
            VisitingDueOn = loan.DueOn,
        };
        db.Books.Add(book);
        await db.SaveChangesAsync(cancellationToken);

        if (loan.HasEbook && await client.DownloadAsync(link, $"/federation/loans/{loan.BookId}/ebook?readerId={db.ReaderId}", EbookStore.MaxBytes, cancellationToken) is { } ebookPath)
        {
            try
            {
                await using var file = File.OpenRead(ebookPath);
                var saved = await ebooks.SaveAsync(file, loan.EbookName ?? "book.epub", cancellationToken);
                if (saved.StoredName is { } stored)
                {
                    await EbookEndpoints.AttachAsync(db, ebooks, book, stored, saved.FileName ?? loan.EbookName ?? "book.epub", cancellationToken);
                }
            }
            finally
            {
                File.Delete(ebookPath);
            }
        }

        if (loan.HasAudio && await client.DownloadAsync(link, $"/federation/loans/{loan.BookId}/audio?readerId={db.ReaderId}", AudioStore.MaxBytes, cancellationToken) is { } zipPath)
        {
            try
            {
                await using var zip = File.OpenRead(zipPath);
                var saved = await audio.SaveAsync([new AudioUpload("tracks.zip", zip, zip.Length)], cancellationToken, keepOrder: true);
                if (saved.StoredName is { } stored)
                {
                    await AudioEndpoints.AttachAsync(db, audio, covers, book, stored, loan.AudioName ?? saved.FileName ?? "audio", cancellationToken);
                }
            }
            finally
            {
                File.Delete(zipPath);
            }
        }

        return book;
    }

    // A copy goes back: its files go, and with them the marks of whose it was; the reader's notes stay.
    private void Return(Book book)
    {
        ebooks.Delete(book.EbookStoredName);
        audio.Delete(book.AudioStoredName);
        (book.EbookStoredName, book.EbookFileName, book.AudioStoredName, book.AudioFileName, book.AudioChapterNames) = (null, null, null, null, null);
        (book.VisitingLinkId, book.VisitingBookId, book.VisitingDueOn) = (null, null, null);
        book.Status = book.Status == BookStatus.Reading ? BookStatus.Finished : book.Status;
    }

    // Every hour, each reader's loans from linked shelves are brought up to date.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                List<int> readers;
                await using (var scope = scopes.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
                    readers = await db.LinkedAsks.Select(ask => ask.ReaderId)
                        .Union(db.Books.IgnoreQueryFilters().Where(book => book.VisitingLinkId != null && book.OwnerId != null).Select(book => book.OwnerId!.Value))
                        .ToListAsync(stoppingToken);
                }

                foreach (var readerId in readers)
                {
                    // One reader's linked shelf being down or refusing is theirs alone: the readers after them still go.
                    try
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
                        await SyncAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), stoppingToken);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning(ex, "Could not bring reader {ReaderId}'s loans from linked shelves up to date; the others go on.", readerId);
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not bring loans from linked shelves up to date; it will try again in an hour.");
            }
        }
    }

    private static string Fit(string text, int most) => text.Trim() is var trimmed && trimmed.Length > most ? trimmed[..most] : text.Trim();
}

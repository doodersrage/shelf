using System.Globalization;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// An audiobook as a private podcast, as Audiobookshelf opens a feed for one: an address with a secret in it, for any
// podcast app, giving the recording, its tracks in order as episodes, and its cover, and nothing else. Only a hash of
// the secret is kept, so the address is shown once; a new one stops the old. The place isn't kept in step: a podcast
// app keeps its own.
public sealed class AudioFeed
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public Book? Book { get; set; }
    public int OwnerId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // When the address stops by itself; never, when not given.
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed record AudioFeedState(bool On, DateTimeOffset? Since, DateTimeOffset? Until);

// The feed for a podcast app, and a page that plays the book in any browser, under the same secret.
public sealed record AudioFeedMade(string Url, string PageUrl);

public sealed record AudioFeedRequest(int? Days);

public static class AudioFeeds
{
    private static readonly XNamespace Itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
    private static readonly XNamespace Psc = "http://podlove.org/simple-chapters";

    public static void Map(RouteGroupBuilder books)
    {
        books.MapGet("/{id:int}/audio/feed", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await StateAsync(db, id, cancellationToken) is { } state ? TypedResults.Ok(state) : TypedResults.NotFound()).WithTags("Sharing");
        books.MapPost("/{id:int}/audio/feed", async Task<IResult> (int id, AudioFeedRequest? request, HttpContext http, ShelfDb db, CancellationToken cancellationToken) =>
            await MakeAsync(db, id, request?.Days is int days and > 0 ? TimeSpan.FromDays(Math.Min(days, 3650)) : null, cancellationToken) switch
            {
                { Token: { } token } => TypedResults.Ok(new AudioFeedMade(Address(http.Request, token), Page(http.Request, token))),
                { Problem: { } problem } => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Feed"] = [problem] }),
                _ => TypedResults.NotFound(),
            }).WithTags("Sharing");
        books.MapDelete("/{id:int}/audio/feed", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await StopAsync(db, id, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound()).WithTags("Sharing");
    }

    // The addresses a podcast app uses, open to anyone with the secret.
    public static void MapPublic(WebApplication app)
    {
        app.MapGet("/listen-feed/{token}/feed.xml", Feed).AllowAnonymous().ExcludeFromDescription();
        app.MapGet("/listen-feed/{token}", Listen).AllowAnonymous().ExcludeFromDescription();
        app.MapGet("/listen-feed/{token}/tracks/{index:int}/{name}", Track).AllowAnonymous().ExcludeFromDescription();
        app.MapGet("/listen-feed/{token}/cover", Cover).AllowAnonymous().ExcludeFromDescription();
    }

    public static string Address(HttpRequest request, string token) => $"{request.Scheme}://{request.Host}{request.PathBase}/listen-feed/{token}/feed.xml";

    public static string Page(HttpRequest request, string token) => $"{request.Scheme}://{request.Host}{request.PathBase}/listen-feed/{token}";

    public static async Task<AudioFeedState?> StateAsync(ShelfDb db, int bookId, CancellationToken cancellationToken = default)
    {
        if (!await db.Books.AnyAsync(book => book.Id == bookId, cancellationToken))
        {
            return null;
        }

        var feed = await db.AudioFeeds.AsNoTracking().FirstOrDefaultAsync(item => item.BookId == bookId, cancellationToken);
        return new AudioFeedState(feed is not null && !(feed.ExpiresAt < DateTimeOffset.UtcNow), feed?.CreatedAt, feed?.ExpiresAt);
    }

    // A new address for the book's feed, shown once: the one before stops. Only a book of the reader's own, with a
    // recording, and not for a child's account.
    public static async Task<(string? Token, string? Problem)> MakeAsync(ShelfDb db, int bookId, TimeSpan? lasts = null, CancellationToken cancellationToken = default)
    {
        var book = await db.Books.AsNoTracking().FirstOrDefaultAsync(item => item.Id == bookId, cancellationToken);
        if (book is null)
        {
            return (null, null);
        }

        if (book.AudioStoredName is null)
        {
            return (null, T("A podcast feed needs a recording."));
        }

        if (await db.Readers.AsNoTracking().AnyAsync(reader => reader.Id == db.ReaderId && reader.Restricted, cancellationToken))
        {
            return (null, T("A child's account cannot do that. Ask the grown-up who made it."));
        }

        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
        await db.AudioFeeds.Where(item => item.BookId == bookId).ExecuteDeleteAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        db.AudioFeeds.Add(new AudioFeed { BookId = bookId, OwnerId = db.ReaderId, TokenHash = Feeds.Hash(token), CreatedAt = now, ExpiresAt = lasts is { } span ? now + span : null });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Asked twice at once: the other made its address; this one says so rather than failing.
            return (null, T("Another address was made for this book just now. Make one again if you need it."));
        }

        return (token, null);
    }

    public static async Task<bool> StopAsync(ShelfDb db, int bookId, CancellationToken cancellationToken = default) =>
        await db.Books.AnyAsync(book => book.Id == bookId, cancellationToken)
        && await db.AudioFeeds.Where(item => item.BookId == bookId).ExecuteDeleteAsync(cancellationToken) >= 0;

    // The book a feed address opens: still its owner's, with its recording, and the owner not suspended.
    private static async Task<Book?> OpenAsync(ShelfDb db, string token, CancellationToken cancellationToken)
    {
        if (token.Length is < 20 or > 60)
        {
            return null;
        }

        var hash = Feeds.Hash(token);
        var feed = await db.AudioFeeds.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(item => item.TokenHash == hash, cancellationToken);
        if (feed is null || feed.ExpiresAt < DateTimeOffset.UtcNow || await db.Readers.AsNoTracking().AnyAsync(reader => reader.Id == feed.OwnerId && (reader.Suspended || reader.Restricted), cancellationToken))
        {
            return null;
        }

        var book = await db.Books.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(item => item.Id == feed.BookId && item.OwnerId == feed.OwnerId, cancellationToken);
        return book?.AudioStoredName is null ? null : book;
    }

    // Nothing under a secret address is kept by a browser or a proxy, so one stopped stops at once.
    private static void Private(HttpResponse response) => response.Headers.CacheControl = "private, no-store";

    // Text as XML may hold it: a stray control character in a file's tag would otherwise stop the whole feed.
    private static string Clean(string? text) => new((text ?? "").Where(XmlConvert.IsXmlChar).ToArray());

    // RSS as podcast apps read it: a serial show, so the first track comes first, an episode a track.
    private static async Task<IResult> Feed(string token, HttpRequest request, ShelfDb db, AudioStore audio, CancellationToken cancellationToken)
    {
        Private(request.HttpContext.Response);
        if (await OpenAsync(db, token, cancellationToken) is not { } book)
        {
            return Results.NotFound();
        }

        var root = $"{request.Scheme}://{request.Host}{request.PathBase}/listen-feed/{token}";
        var tracks = audio.Tracks(book.AudioStoredName);
        var lengths = audio.Lengths(book.AudioStoredName);
        var channel = new XElement("channel",
            new XElement("title", Clean(book.Title)),
            new XElement("link", root + "/feed.xml"),
            new XElement("description", Clean(string.IsNullOrWhiteSpace(book.Subtitle) ? T("{0}, by {1}, from Shelf.", book.Title, book.Author) : book.Subtitle)),
            new XElement(Itunes + "author", Clean(book.Author)),
            new XElement(Itunes + "type", "serial"),
            new XElement(Itunes + "block", "yes"),
            new XElement(Itunes + "image", new XAttribute("href", root + "/cover")),
            new XElement("image", new XElement("url", root + "/cover"), new XElement("title", Clean(book.Title)), new XElement("link", root + "/feed.xml")));
        if (book.Language is { } language && EpubDetails.LanguageCode(language) is { } code)
        {
            channel.Add(new XElement("language", code));
        }

        // A single file's chapters, as Podlove's simple chapters, which many apps show.
        var marks = tracks.Count == 1
            ? (Audible.Place(book.AudioChapterNames, lengths) is { } placed ? placed.Select(chapter => (chapter.Title, chapter.Start)).ToList() : audio.Chapters(book.AudioStoredName).Select(chapter => (chapter.Title, chapter.Start)).ToList())
            : [];
        foreach (var track in tracks)
        {
            var path = audio.TrackPath(book.AudioStoredName, track.Index)!;
            var name = $"{track.Index + 1:000}{Path.GetExtension(path).ToLowerInvariant()}";
            var item = new XElement("item",
                new XElement("title", Clean(tracks.Count == 1 ? book.Title : AudioStore.PlainTitle(track.Title))),
                // An episode's id that says nothing of how the shelf keeps its files.
                new XElement("guid", new XAttribute("isPermaLink", "false"), Feeds.Hash($"{token}|{book.AudioStoredName}|{track.Index}")[..32]),
                // Each a minute after the one before, so apps that go by date keep them in order.
                new XElement("pubDate", book.AddedAt.AddMinutes(track.Index).ToString("r", CultureInfo.InvariantCulture)),
                new XElement("enclosure", new XAttribute("url", $"{root}/tracks/{track.Index}/{name}"), new XAttribute("length", new FileInfo(path).Length), new XAttribute("type", AudioStore.ContentType(path))),
                new XElement(Itunes + "episode", track.Index + 1),
                new XElement(Itunes + "episodeType", "full"));
            if (lengths[track.Index] is double seconds)
            {
                item.Add(new XElement(Itunes + "duration", ((int)seconds).ToString(CultureInfo.InvariantCulture)));
            }

            if (marks.Count > 1)
            {
                item.Add(new XElement(Psc + "chapters", new XAttribute("version", "1.2"),
                    marks.Select(mark => new XElement(Psc + "chapter", new XAttribute("start", TimeSpan.FromSeconds(mark.Start).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture)), new XAttribute("title", Clean(mark.Title))))));
            }

            channel.Add(item);
        }

        var rss = new XElement("rss", new XAttribute("version", "2.0"), new XAttribute(XNamespace.Xmlns + "itunes", Itunes), new XAttribute(XNamespace.Xmlns + "psc", Psc), channel);
        return Results.Text(new XDocument(new XDeclaration("1.0", "utf-8", null), rss).Declaration + "\n" + rss, "application/rss+xml; charset=utf-8");
    }

    // The book in any browser, for someone it's shared with: its cover, title, and a player for each track. A plain
    // page with nothing to run, so it opens anywhere and asks for nothing.
    private static async Task<IResult> Listen(string token, HttpRequest request, ShelfDb db, AudioStore audio, CancellationToken cancellationToken)
    {
        Private(request.HttpContext.Response);
        if (await OpenAsync(db, token, cancellationToken) is not { } book)
        {
            return Results.NotFound();
        }

        // Only its own pictures, sound, and style, never framed, and no address passed on from it.
        request.HttpContext.Response.Headers.ContentSecurityPolicy = "default-src 'none'; img-src 'self'; media-src 'self'; style-src 'unsafe-inline'; frame-ancestors 'none'";
        request.HttpContext.Response.Headers["Referrer-Policy"] = "no-referrer";

        var encode = System.Text.Encodings.Web.HtmlEncoder.Default;
        var root = $"/listen-feed/{token}";
        var tracks = audio.Tracks(book.AudioStoredName);
        var language = EpubDetails.LanguageCode(book.Language) ?? "en";
        var html = new System.Text.StringBuilder()
            .Append("<!DOCTYPE html><html lang=\"").Append(encode.Encode(language)).Append("\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append("<meta name=\"robots\" content=\"noindex\"><title>").Append(encode.Encode(book.Title)).Append("</title>")
            .Append("<style>body{font-family:system-ui,sans-serif;max-width:36rem;margin:2rem auto;padding:0 1rem;line-height:1.5;color:#252a28;background:#f5f1e8}img{max-width:12rem;border-radius:8px;display:block;margin-bottom:1rem}audio{width:100%}ol{padding-left:1.25rem}li{margin:.75rem 0}.hint{color:#66716b}</style></head><body>")
            .Append("<img src=\"").Append(root).Append("/cover\" alt=\"\">")
            .Append("<h1>").Append(encode.Encode(book.Title)).Append("</h1><p class=\"hint\">").Append(encode.Encode(book.Author)).Append("</p>");
        if (tracks.Count == 1)
        {
            var path = audio.TrackPath(book.AudioStoredName, 0)!;
            html.Append("<audio controls preload=\"metadata\" src=\"").Append(root).Append("/tracks/0/001").Append(Path.GetExtension(path).ToLowerInvariant()).Append("\"></audio>");
        }
        else
        {
            html.Append("<ol>");
            foreach (var track in tracks)
            {
                var path = audio.TrackPath(book.AudioStoredName, track.Index)!;
                html.Append("<li>").Append(encode.Encode(AudioStore.PlainTitle(track.Title)))
                    .Append("<audio controls preload=\"none\" src=\"").Append(root).Append("/tracks/").Append(track.Index).Append('/').Append($"{track.Index + 1:000}").Append(Path.GetExtension(path).ToLowerInvariant()).Append("\"></audio></li>");
            }

            html.Append("</ol>");
        }

        html.Append("<p class=\"hint\">").Append(encode.Encode(T("Shared from Shelf. In a podcast app, add {0}.", Address(request, token)))).Append("</p></body></html>");
        return Results.Content(html.ToString(), "text/html; charset=utf-8");
    }

    private static async Task<IResult> Track(string token, int index, string name, HttpContext http, ShelfDb db, AudioStore audio, CancellationToken cancellationToken)
    {
        Private(http.Response);
        return await OpenAsync(db, token, cancellationToken) is { } book && audio.TrackPath(book.AudioStoredName, index) is { } path
            ? Results.File(path, AudioStore.ContentType(path), enableRangeProcessing: true)
            : Results.NotFound();
    }

    private static async Task<IResult> Cover(string token, HttpContext http, ShelfDb db, EbookStore ebooks, CoverStore covers, CancellationToken cancellationToken)
    {
        Private(http.Response);
        return await OpenAsync(db, token, cancellationToken) is { } book && Covers.Picture(book, ebooks, covers) is { } picture
            ? Results.File(picture, picture.AsSpan().StartsWith((byte[])[0x89, 0x50, 0x4E, 0x47]) ? "image/png" : "image/jpeg")
            : Results.NotFound();
    }
}

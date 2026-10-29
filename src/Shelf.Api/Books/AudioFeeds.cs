using System.Globalization;
using System.Security.Cryptography;
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
}

public sealed record AudioFeedState(bool On, DateTimeOffset? Since);

public sealed record AudioFeedMade(string Url);

public static class AudioFeeds
{
    private static readonly XNamespace Itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
    private static readonly XNamespace Psc = "http://podlove.org/simple-chapters";

    public static void Map(RouteGroupBuilder books)
    {
        books.MapGet("/{id:int}/audio/feed", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await StateAsync(db, id, cancellationToken) is { } state ? TypedResults.Ok(state) : TypedResults.NotFound()).WithTags("Sharing");
        books.MapPost("/{id:int}/audio/feed", async Task<IResult> (int id, HttpContext http, ShelfDb db, CancellationToken cancellationToken) =>
            await MakeAsync(db, id, cancellationToken) switch
            {
                { Token: { } token } => TypedResults.Ok(new AudioFeedMade(Address(http.Request, token))),
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
        app.MapGet("/listen-feed/{token}/tracks/{index:int}/{name}", Track).AllowAnonymous().ExcludeFromDescription();
        app.MapGet("/listen-feed/{token}/cover", Cover).AllowAnonymous().ExcludeFromDescription();
    }

    public static string Address(HttpRequest request, string token) => $"{request.Scheme}://{request.Host}{request.PathBase}/listen-feed/{token}/feed.xml";

    public static async Task<AudioFeedState?> StateAsync(ShelfDb db, int bookId, CancellationToken cancellationToken = default)
    {
        if (!await db.Books.AnyAsync(book => book.Id == bookId, cancellationToken))
        {
            return null;
        }

        var feed = await db.AudioFeeds.AsNoTracking().FirstOrDefaultAsync(item => item.BookId == bookId, cancellationToken);
        return new AudioFeedState(feed is not null, feed?.CreatedAt);
    }

    // A new address for the book's feed, shown once: the one before stops. Only a book of the reader's own, with a
    // recording, and not for a child's account.
    public static async Task<(string? Token, string? Problem)> MakeAsync(ShelfDb db, int bookId, CancellationToken cancellationToken = default)
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
        db.AudioFeeds.Add(new AudioFeed { BookId = bookId, OwnerId = db.ReaderId, TokenHash = Feeds.Hash(token), CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
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
        if (feed is null || await db.Readers.AsNoTracking().AnyAsync(reader => reader.Id == feed.OwnerId && (reader.Suspended || reader.Restricted), cancellationToken))
        {
            return null;
        }

        var book = await db.Books.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(item => item.Id == feed.BookId && item.OwnerId == feed.OwnerId, cancellationToken);
        return book?.AudioStoredName is null ? null : book;
    }

    // RSS as podcast apps read it: a serial show, so the first track comes first, an episode a track.
    private static async Task<IResult> Feed(string token, HttpRequest request, ShelfDb db, AudioStore audio, CancellationToken cancellationToken)
    {
        if (await OpenAsync(db, token, cancellationToken) is not { } book)
        {
            return Results.NotFound();
        }

        var root = $"{request.Scheme}://{request.Host}{request.PathBase}/listen-feed/{token}";
        var tracks = audio.Tracks(book.AudioStoredName);
        var lengths = audio.Lengths(book.AudioStoredName);
        var channel = new XElement("channel",
            new XElement("title", book.Title),
            new XElement("link", root + "/feed.xml"),
            new XElement("description", string.IsNullOrWhiteSpace(book.Subtitle) ? T("{0}, by {1}, from Shelf.", book.Title, book.Author) : book.Subtitle),
            new XElement(Itunes + "author", book.Author),
            new XElement(Itunes + "type", "serial"),
            new XElement(Itunes + "block", "yes"),
            new XElement(Itunes + "image", new XAttribute("href", root + "/cover")),
            new XElement("image", new XElement("url", root + "/cover"), new XElement("title", book.Title), new XElement("link", root + "/feed.xml")));
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
                new XElement("title", tracks.Count == 1 ? book.Title : AudioStore.PlainTitle(track.Title)),
                new XElement("guid", new XAttribute("isPermaLink", "false"), $"shelf-{book.Id}-{book.AudioStoredName}-{track.Index}"),
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
                    marks.Select(mark => new XElement(Psc + "chapter", new XAttribute("start", TimeSpan.FromSeconds(mark.Start).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture)), new XAttribute("title", mark.Title)))));
            }

            channel.Add(item);
        }

        var rss = new XElement("rss", new XAttribute("version", "2.0"), new XAttribute(XNamespace.Xmlns + "itunes", Itunes), new XAttribute(XNamespace.Xmlns + "psc", Psc), channel);
        return Results.Text(new XDocument(new XDeclaration("1.0", "utf-8", null), rss).Declaration + "\n" + rss, "application/rss+xml; charset=utf-8");
    }

    private static async Task<IResult> Track(string token, int index, string name, ShelfDb db, AudioStore audio, CancellationToken cancellationToken) =>
        await OpenAsync(db, token, cancellationToken) is { } book && audio.TrackPath(book.AudioStoredName, index) is { } path
            ? Results.File(path, AudioStore.ContentType(path), enableRangeProcessing: true)
            : Results.NotFound();

    private static async Task<IResult> Cover(string token, ShelfDb db, EbookStore ebooks, CoverStore covers, CancellationToken cancellationToken) =>
        await OpenAsync(db, token, cancellationToken) is { } book && Covers.Picture(book, ebooks, covers) is { } picture
            ? Results.File(picture, picture.AsSpan().StartsWith((byte[])[0x89, 0x50, 0x4E, 0x47]) ? "image/png" : "image/jpeg")
            : Results.NotFound();
}

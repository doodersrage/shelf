using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Books;

// The last place a KOReader device sent for a book, kept as KOReader wrote it so the device gets it back exactly.
public sealed class KosyncPlace
{
    public int Id { get; set; }
    public int ReaderId { get; set; }
    public int BookId { get; set; }
    public required string Progress { get; set; }
    public double Percentage { get; set; }
    // The chapter, or BeforeReread once the book is read again: the place KOReader said is then from the last time
    // through, so the device saying it again is not taken as reading on, and isn't handed back as somewhere to go.
    public const int BeforeReread = -1;
    public int Chapter { get; set; }
    public string? Device { get; set; }
    public string? DeviceId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed record KosyncProgress(
    string? Document,
    string? Progress,
    double? Percentage,
    string? Device,
    [property: JsonPropertyName("device_id")] string? DeviceId);

// KOReader's progress sync (its "kosync" plugin), so a place reached on an e-reader comes back to Shelf and the other
// way round. Point KOReader's custom sync server at https://<shelf>/kosync and sign in with the reader's name and
// the KOReader password from Devices. KOReader sends that password's MD5, so only a hash of the MD5 is kept.
public static partial class Kosync
{
    public const string AcceptType = "application/vnd.koreader.v1+json";

    public static void Map(WebApplication app)
    {
        var kosync = app.MapGroup("/kosync").AllowAnonymous().DisableAntiforgery();
        kosync.MapPost("/users/create", () => Results.Json(
            new { code = 2005, message = "Make an account on Shelf, then use the KOReader password from its Devices page." },
            statusCode: StatusCodes.Status403Forbidden));
        kosync.MapGet("/users/auth", Auth);
        kosync.MapPut("/syncs/progress", Put);
        kosync.MapGet("/syncs/progress/{document}", Get);
    }

    public static string NewPassword() => ReaderRules.NewPassword();

    public static string HashOf(string password) => HashKey(Md5(password));

    private static string HashKey(string md5) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(md5.Trim().ToLowerInvariant()))).ToLowerInvariant();

    private static string Md5(string text) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    // KOReader's document id: the MD5 of 1 KB samples at 0, then 1 KB, 4 KB, 16 KB, and on by fours, up to the end.
    public static string? Digest(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        using var file = File.OpenRead(path);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[1024];
        for (var i = -1; i <= 10; i++)
        {
            var offset = i < 0 ? 0L : 1024L << (2 * i);
            if (offset >= file.Length)
            {
                break;
            }

            file.Position = offset;
            var read = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            md5.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<Reader?> SignedInAsync(HttpContext http, ShelfDb db, ShelfReader shelfReader, CancellationToken cancellationToken)
    {
        var name = http.Request.Headers["x-auth-user"].ToString().Trim();
        var key = http.Request.Headers["x-auth-key"].ToString();
        if (name.Length == 0 || key.Length == 0)
        {
            return null;
        }

        var reader = await ReaderRules.FindAsync(db, name, cancellationToken);
        if (reader?.KosyncHash is null || reader.Suspended
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(reader.KosyncHash), Encoding.ASCII.GetBytes(HashKey(key))))
        {
            return null;
        }

        shelfReader.Use(reader.Id);
        return reader;
    }

    private static IResult Unauthorized() =>
        Results.Json(new { code = 2001, message = "Unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);

    private static async Task<IResult> Auth(HttpContext http, ShelfDb db, ShelfReader shelfReader, CancellationToken cancellationToken) =>
        await SignedInAsync(http, db, shelfReader, cancellationToken) is null ? Unauthorized() : Results.Json(new { authorized = "OK" });

    // The reader's own, borrowed, or shared e-book KOReader calls this document: by its sampled MD5, or by its file
    // name's. A shared one keeps a place of the reader's own, as in Shelf's reader.
    private static async Task<Book?> FindAsync(ShelfDb db, string document, CancellationToken cancellationToken)
    {
        var me = db.ReaderId;
        var shared = await Lending.SharedIdsAsync(db, cancellationToken);
        var books = await db.Books.IgnoreQueryFilters()
            .Where(book => (book.OwnerId == me || book.BorrowerId == me || shared.Contains(book.Id)) && book.EbookStoredName != null)
            .Select(book => new { book.Id, book.KoreaderDigest, book.EbookFileName })
            .ToListAsync(cancellationToken);
        var match = books.FirstOrDefault(book => book.KoreaderDigest == document)
            ?? books.FirstOrDefault(book => book.EbookFileName is not null && Md5(book.EbookFileName) == document);
        return match is null ? null : await db.Books.IgnoreQueryFilters().FirstAsync(book => book.Id == match.Id, cancellationToken);
    }

    private static async Task<IResult> Put(
        KosyncProgress progress, HttpContext http, ShelfDb db, ShelfReader shelfReader, EbookStore store, CancellationToken cancellationToken)
    {
        if (await SignedInAsync(http, db, shelfReader, cancellationToken) is not { } reader)
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(progress.Document) || string.IsNullOrWhiteSpace(progress.Progress) || progress.Progress.Length > 500)
        {
            return Results.Json(new { code = 2003, message = "Invalid request" }, statusCode: StatusCodes.Status400BadRequest);
        }

        var now = DateTimeOffset.UtcNow;
        if (await FindAsync(db, progress.Document, cancellationToken) is { } book && await Lending.OpenAsync(db, book.Id, cancellationToken) is { } open)
        {
            var chapter = ChapterOf(progress.Progress, progress.Percentage, Count(store, book));
            var percentage = Math.Clamp(progress.Percentage ?? 0, 0, 1);
            // The very place in an EPUB, as the reader in Shelf keeps it, when the chapter can be read.
            var cfi = EpubPath(store, book) is { } epub && EpubFile.ChapterDocument(epub, chapter) is { } xhtml ? KoreaderPlace.ToCfi(progress.Progress, chapter, xhtml) : null;
            var kept = await db.KosyncPlaces.FirstOrDefaultAsync(item => item.ReaderId == reader.Id && item.BookId == book.Id, cancellationToken);

            // The book read again, and KOReader still at the end of the last time through: kept as it was, and the
            // reread's place left alone. Anywhere else it says is reading on, as ever.
            if (kept is { Chapter: KosyncPlace.BeforeReread } && kept.Progress == progress.Progress)
            {
                return Results.Json(new { document = progress.Document, timestamp = now.ToUnixTimeSeconds() });
            }

            if (kept is null)
            {
                kept = new KosyncPlace { ReaderId = reader.Id, BookId = book.Id, Progress = progress.Progress };
                db.KosyncPlaces.Add(kept);
            }

            kept.Progress = progress.Progress;
            kept.Percentage = percentage;
            kept.Chapter = chapter;
            kept.Device = Fit(progress.Device);
            kept.DeviceId = Fit(progress.DeviceId);
            kept.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            using var _ = Positions.Reporting("KOReader", kept.Device);
            await Lending.KeepPlaceAsync(db, open, place =>
            {
                // On, never back: a later chapter, or further into the same one.
                var further = chapter > (place.EbookChapter ?? -1)
                    || (chapter == place.EbookChapter && cfi is not null && (place.EbookCfi is null || KoreaderPlace.Compare(cfi, place.EbookCfi) is null or > 0));
                if (further)
                {
                    place.EbookChapter = chapter;
                    if (cfi is { Length: <= 1000 })
                    {
                        (place.EbookCfi, place.EbookProgress) = (cfi, percentage);
                    }
                }
            }, cancellationToken);
        }

        // A document Shelf does not have is acknowledged and forgotten, as the reference server would keep it.
        return Results.Json(new { document = progress.Document, timestamp = now.ToUnixTimeSeconds() });
    }

    private static async Task<IResult> Get(
        string document, HttpContext http, ShelfDb db, ShelfReader shelfReader, EbookStore store, CancellationToken cancellationToken)
    {
        if (await SignedInAsync(http, db, shelfReader, cancellationToken) is not { } reader)
        {
            return Unauthorized();
        }

        if (await FindAsync(db, document, cancellationToken) is not { } book || await Lending.OpenAsync(db, book.Id, cancellationToken) is not { } open)
        {
            return Results.Json(new { });
        }

        var shelfPlace = await Lending.PlaceAsync(db, open, cancellationToken);
        var chapter = shelfPlace.EbookChapter ?? 0;
        var kept = await db.KosyncPlaces.AsNoTracking().FirstOrDefaultAsync(item => item.ReaderId == reader.Id && item.BookId == book.Id, cancellationToken);
        var deviceFurther = kept is not null && (kept.Chapter > chapter || (kept.Chapter == chapter && DeviceFurther(kept, shelfPlace, store, book)));
        if (kept is not null && deviceFurther)
        {
            return Results.Json(new
            {
                document,
                progress = kept.Progress,
                percentage = kept.Percentage,
                device = kept.Device,
                device_id = kept.DeviceId,
                timestamp = kept.UpdatedAt.ToUnixTimeSeconds(),
            });
        }

        if (kept is null && chapter == 0)
        {
            return Results.Json(new { });
        }

        // Read further in Shelf than on the device, in an EPUB to the word: the very place, as KOReader writes it.
        if (shelfPlace.EbookCfi is { } shelfCfi
            && KoreaderPlace.ChapterOfCfi(shelfCfi) is { } cfiChapter
            && EpubPath(store, book) is { } epub
            && EpubFile.ChapterDocument(epub, cfiChapter) is { } xhtml
            && KoreaderPlace.ToXPointer(shelfCfi, xhtml) is { } pointer)
        {
            return Results.Json(new
            {
                document,
                progress = pointer,
                percentage = Math.Round(shelfPlace.EbookProgress ?? 0, 4),
                device = "Shelf",
                device_id = "shelf",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
        }

        // Otherwise the start of that chapter, or that page of a PDF.
        var count = Count(store, book);
        var pdf = EbookStore.IsPdf(book.EbookStoredName) || EbookStore.IsComic(book.EbookStoredName);
        return Results.Json(new
        {
            document,
            progress = pdf ? (chapter + 1).ToString(CultureInfo.InvariantCulture) : $"/body/DocFragment[{chapter + 1}]/body",
            percentage = count > 0 ? Math.Round((double)chapter / count, 4) : 0,
            device = "Shelf",
            device_id = "shelf",
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
    }

    // An EPUB place is an XPointer into DocFragment[n], the nth file of the spine; a PDF's is a page number.
    public static int ChapterOf(string progress, double? percentage, int count)
    {
        var fragment = DocFragment().Match(progress);
        if (fragment.Success && int.TryParse(fragment.Groups[1].Value, out var spine) && spine >= 1)
        {
            return spine - 1;
        }

        if (int.TryParse(progress.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page >= 1)
        {
            return page - 1;
        }

        return count > 0 && percentage is > 0 ? Math.Min(count - 1, (int)Math.Floor(percentage.Value * count)) : 0;
    }

    private static int Count(EbookStore store, Book book)
    {
        if (EbookStore.IsEpub(book.EbookStoredName))
        {
            return EpubFile.Chapters(store.OpenPath(book.EbookStoredName) ?? "")?.Count ?? 0;
        }

        if (EbookStore.IsComic(book.EbookStoredName))
        {
            return ComicFile.Pages(store.OpenPath(book.EbookStoredName)).Count;
        }

        return book.Pages ?? 0;
    }

    // In the same chapter: the device's place is further unless Shelf's is a CFI past it, both read as paths in the
    // chapter; the two books' shares are counted differently, KOReader's by its pages and Shelf's by its words.
    private static bool DeviceFurther(KosyncPlace kept, LoanPlace shelfPlace, EbookStore store, Book book)
    {
        if (shelfPlace.EbookCfi is not { } shelfCfi)
        {
            return true;
        }

        var deviceCfi = EpubPath(store, book) is { } epub && EpubFile.ChapterDocument(epub, kept.Chapter) is { } xhtml ? KoreaderPlace.ToCfi(kept.Progress, kept.Chapter, xhtml) : null;
        return deviceCfi is not null && KoreaderPlace.Compare(deviceCfi, shelfCfi) is { } order
            ? order >= 0
            : kept.Percentage >= (shelfPlace.EbookProgress ?? 0);
    }

    private static string? EpubPath(EbookStore store, Book book) =>
        EbookStore.IsEpub(book.EbookStoredName) ? store.OpenPath(book.EbookStoredName) : null;

    private static string? Fit(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Length > 100 ? value[..100] : value;

    [GeneratedRegex(@"DocFragment\[(\d+)\]")]
    private static partial Regex DocFragment();
}

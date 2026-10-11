using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// What one Kobo knows of one book: that it was sent, whether the reader archived it on the device, and the place the
// device last sent, kept as the device wrote it so it gets it back exactly.
public sealed class KoboEntry
{
    public int Id { get; set; }
    public int ReaderId { get; set; }
    public int BookId { get; set; }
    public bool Archived { get; set; }
    public string? Bookmark { get; set; }
    public string? Status { get; set; }
    public DateTimeOffset SyncedAt { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
}

// A Kobo e-reader's own store, stood in for, as Calibre-Web does: the reader sets the Kobo's api_endpoint to
// /kobo/<token>, and the Kobo's sync then sees their EPUBs (all, or one collection) as books bought, downloads them,
// and sends back where it stopped. Whatever else the Kobo asks of its store gets an empty answer.
public static partial class Kobo
{
    public static void Map(WebApplication app)
    {
        var kobo = app.MapGroup("/kobo/{token}").AllowAnonymous().DisableAntiforgery().ExcludeFromDescription().AddEndpointFilter(Signed);
        kobo.MapPost("/v1/auth/device", Device);
        kobo.MapGet("/v1/initialization", Initialization);
        kobo.MapGet("/v1/library/sync", Sync);
        kobo.MapGet("/v1/library/{uuid:guid}/metadata", Metadata);
        kobo.MapGet("/v1/library/{uuid:guid}/state", GetState);
        kobo.MapPut("/v1/library/{uuid:guid}/state", PutState);
        kobo.MapDelete("/v1/library/{uuid:guid}", Archive);
        kobo.MapGet("/download/{id:int}/epub", Download);
        kobo.MapGet("/{uuid:guid}/{width:int}/{height:int}/{**rest}", Cover);
        kobo.Map("/{**rest}", () => TypedResults.Json(new { }));
    }

    // The token in the address names the reader; every request is theirs, or refused.
    private static async ValueTask<object?> Signed(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var token = http.GetRouteValue("token") as string;
        var db = http.RequestServices.GetRequiredService<ShelfDb>();
        var hash = Hash(token ?? "");
        var reader = token is { Length: > 0 and <= 100 } ? await db.Readers.AsNoTracking().FirstOrDefaultAsync(item => item.KoboTokenHash == hash, http.RequestAborted) : null;
        if (reader is null)
        {
            return TypedResults.Unauthorized();
        }

        http.RequestServices.GetRequiredService<ShelfReader>().Use(reader.Id);
        http.Items["kobo-reader"] = reader;
        return await next(context);
    }

    public static async Task<string> MakeTokenAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var reader = await db.Readers.FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        reader.KoboTokenHash = Hash(token);
        await db.SaveChangesAsync(cancellationToken);
        await Audit.NoteAsync(db, Say("Made a Kobo link"), reader, cancellationToken: cancellationToken);
        return token;
    }

    public static async Task StopAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var reader = await db.Readers.FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        reader.KoboTokenHash = null;
        await db.KoboEntries.Where(entry => entry.ReaderId == reader.Id).ExecuteDeleteAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    // A book's id on the Kobo: the same for the same book every time.
    public static Guid UuidOf(int bookId) => new(MD5.HashData(Encoding.UTF8.GetBytes($"shelf-kobo-book-{bookId}")));

    // The books the Kobo is to have: the reader's EPUBs, or those in the collection they chose.
    private static async Task<List<Book>> WantedAsync(ShelfDb db, Reader reader, CancellationToken cancellationToken)
    {
        var books = await db.Books.AsNoTracking().Where(book => book.EbookStoredName != null).ToListAsync(cancellationToken);
        books = books.Where(book => EbookStore.IsEpub(book.EbookStoredName)).ToList();
        if (reader.KoboCollectionId is int collection)
        {
            var inside = (await db.CollectionBooks.Where(entry => entry.CollectionId == collection).Select(entry => entry.BookId).ToListAsync(cancellationToken)).ToHashSet();
            books = books.Where(book => inside.Contains(book.Id)).ToList();
        }

        return books;
    }

    // A second parameter keeps this from being taken for a bare RequestDelegate, whose answer would be dropped.
    private static async Task<IResult> Device(HttpContext http, CancellationToken cancellationToken)
    {
        // The Kobo asks to sign in; it only needs some tokens back, as Shelf goes by the one in the address.
        string? userKey = null;
        try
        {
            var asked = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: cancellationToken);
            userKey = (asked as JsonObject)?.FirstOrDefault(pair => string.Equals(pair.Key, "UserKey", StringComparison.OrdinalIgnoreCase)).Value?.GetValue<string>();
        }
        catch (JsonException)
        {
        }

        return TypedResults.Json(new Dictionary<string, object?>
        {
            ["AccessToken"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)),
            ["RefreshToken"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)),
            ["TokenType"] = "Bearer",
            ["TrackingId"] = Guid.NewGuid().ToString(),
            ["UserKey"] = userKey ?? "",
        });
    }

    private static IResult Initialization(HttpContext http, string token)
    {
        var host = $"{http.Request.Scheme}://{http.Request.Host}";
        var here = $"{host}/kobo/{token}";
        var resources = new Dictionary<string, string>(Store);
        resources["image_host"] = host;
        resources["image_url_template"] = here + "/{ImageId}/{Width}/{Height}/false/image.jpg";
        resources["image_url_quality_template"] = here + "/{ImageId}/{Width}/{Height}/{Quality}/{IsGreyscale}/image.jpg";
        resources["library_sync"] = here + "/v1/library/sync";
        resources["library_items"] = here + "/v1/user/library";
        resources["library_book"] = here + "/v1/user/library/books/{LibraryItemId}";
        resources["library_metadata"] = here + "/v1/library/{Ids}/metadata";
        resources["reading_state"] = here + "/v1/library/{Ids}/state";
        resources["user_profile"] = here + "/v1/user/profile";
        resources["device_auth"] = here + "/v1/auth/device";
        http.Response.Headers["x-kobo-apitoken"] = "e30=";
        return TypedResults.Json(new { Resources = resources });
    }

    // Every book the Kobo should have, new or changed, and every one it should no longer have.
    private static async Task<IResult> Sync(HttpContext http, string token, ShelfDb db, CancellationToken cancellationToken)
    {
        var reader = (Reader)http.Items["kobo-reader"]!;
        var host = $"{http.Request.Scheme}://{http.Request.Host}/kobo/{token}";
        var wanted = await WantedAsync(db, reader, cancellationToken);
        var entries = await db.KoboEntries.Where(entry => entry.ReaderId == reader.Id).ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var answer = new JsonArray();

        foreach (var book in wanted)
        {
            var entry = entries.FirstOrDefault(item => item.BookId == book.Id);
            if (entry?.Archived == true)
            {
                continue;
            }

            var isNew = entry is null;
            if (entry is null)
            {
                entry = new KoboEntry { ReaderId = reader.Id, BookId = book.Id, SyncedAt = now };
                db.KoboEntries.Add(entry);
            }

            var size = BookSize(http, book);
            answer.Add(new JsonObject
            {
                [isNew ? "NewEntitlement" : "ChangedEntitlement"] = new JsonObject
                {
                    ["BookEntitlement"] = Entitlement(book, removed: false),
                    ["BookMetadata"] = BookMetadata(book, host, size),
                    ["ReadingState"] = ReadingState(book, entry),
                },
            });
            entry.SyncedAt = now;
        }

        // Books gone from the shelf, or from the chosen collection: the Kobo takes them away.
        var ids = wanted.Select(book => book.Id).ToHashSet();
        foreach (var gone in entries.Where(entry => !ids.Contains(entry.BookId)).ToList())
        {
            var stub = new Book { Id = gone.BookId, Title = "", Author = "", AddedAt = gone.SyncedAt };
            answer.Add(new JsonObject { ["ChangedEntitlement"] = new JsonObject { ["BookEntitlement"] = Entitlement(stub, removed: true) } });
            db.KoboEntries.Remove(gone);
        }

        await db.SaveChangesAsync(cancellationToken);
        http.Response.Headers["x-kobo-synctoken"] = Convert.ToBase64String(Encoding.UTF8.GetBytes($"shelf:{now.ToUnixTimeSeconds()}"));
        return TypedResults.Content(answer.ToJsonString(), "application/json");
    }

    private static async Task<IResult> Metadata(Guid uuid, HttpContext http, string token, ShelfDb db, CancellationToken cancellationToken)
    {
        var reader = (Reader)http.Items["kobo-reader"]!;
        var book = (await WantedAsync(db, reader, cancellationToken)).FirstOrDefault(item => UuidOf(item.Id) == uuid);
        return book is null
            ? TypedResults.NotFound()
            : TypedResults.Content(new JsonArray(BookMetadata(book, $"{http.Request.Scheme}://{http.Request.Host}/kobo/{token}", BookSize(http, book))).ToJsonString(), "application/json");
    }

    private static async Task<IResult> GetState(Guid uuid, HttpContext http, ShelfDb db, CancellationToken cancellationToken)
    {
        var reader = (Reader)http.Items["kobo-reader"]!;
        var book = (await WantedAsync(db, reader, cancellationToken)).FirstOrDefault(item => UuidOf(item.Id) == uuid);
        if (book is null)
        {
            return TypedResults.NotFound();
        }

        var entry = await db.KoboEntries.FirstOrDefaultAsync(item => item.ReaderId == reader.Id && item.BookId == book.Id, cancellationToken);
        return TypedResults.Content(new JsonArray(ReadingState(book, entry)).ToJsonString(), "application/json");
    }

    // Where the Kobo stopped: kept as it said, and turned into Shelf's own place (the chapter its location names)
    // and status (finished, when it says so).
    private static async Task<IResult> PutState(Guid uuid, HttpContext http, ShelfDb db, EbookStore store, CancellationToken cancellationToken)
    {
        var reader = (Reader)http.Items["kobo-reader"]!;
        var book = (await WantedAsync(db, reader, cancellationToken)).FirstOrDefault(item => UuidOf(item.Id) == uuid);
        if (book is null)
        {
            return TypedResults.NotFound();
        }

        JsonNode? said;
        try
        {
            said = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return TypedResults.BadRequest();
        }

        var state = said?["ReadingStates"]?.AsArray().FirstOrDefault();
        var entry = await db.KoboEntries.FirstOrDefaultAsync(item => item.ReaderId == reader.Id && item.BookId == book.Id, cancellationToken);
        if (entry is null)
        {
            entry = new KoboEntry { ReaderId = reader.Id, BookId = book.Id, SyncedAt = DateTimeOffset.UtcNow };
            db.KoboEntries.Add(entry);
        }

        var tracked = await db.Books.FirstAsync(item => item.Id == book.Id, cancellationToken);
        if (state?["CurrentBookmark"] is JsonObject bookmark)
        {
            entry.Bookmark = bookmark.ToJsonString();
            var source = bookmark["Location"]?["Source"]?.GetValue<string>();
            if (source is not null && store.OpenPath(book.EbookStoredName) is { } path && EpubFile.Chapters(path) is { } chapters)
            {
                var file = source.Split('#')[0];
                var chapter = chapters.FirstOrDefault(item => item.Href.EndsWith(file, StringComparison.OrdinalIgnoreCase) || file.EndsWith(item.Href, StringComparison.OrdinalIgnoreCase));
                if (chapter is not null)
                {
                    tracked.EbookChapter = chapter.Index;
                }
            }
        }

        var status = state?["StatusInfo"]?["Status"]?.GetValue<string>();
        if (status is not null)
        {
            entry.Status = status;
            if (status == "Finished")
            {
                BookRules.ChangeStatus(tracked, BookStatus.Finished);
            }
            else if (status == "Reading" && tracked.Status == BookStatus.Want)
            {
                BookRules.ChangeStatus(tracked, BookStatus.Reading);
            }
        }

        entry.ModifiedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        var id = uuid.ToString();
        return TypedResults.Json(new
        {
            RequestResult = "Success",
            UpdateResults = new[]
            {
                new
                {
                    EntitlementId = id,
                    CurrentBookmarkResult = new { Result = "Success" },
                    StatisticsResult = new { Result = "Success" },
                    StatusInfoResult = new { Result = "Success" },
                },
            },
        });
    }

    // Archived on the Kobo: it stays on the shelf, and the Kobo is not sent it again.
    private static async Task<IResult> Archive(Guid uuid, HttpContext http, ShelfDb db, CancellationToken cancellationToken)
    {
        var reader = (Reader)http.Items["kobo-reader"]!;
        var book = (await WantedAsync(db, reader, cancellationToken)).FirstOrDefault(item => UuidOf(item.Id) == uuid);
        var entry = book is null ? null : await db.KoboEntries.FirstOrDefaultAsync(item => item.ReaderId == reader.Id && item.BookId == book.Id, cancellationToken);
        if (entry is not null)
        {
            entry.Archived = true;
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.NoContent();
    }

    private static async Task<IResult> Download(int id, HttpContext http, ShelfDb db, EbookStore store, CancellationToken cancellationToken)
    {
        var reader = (Reader)http.Items["kobo-reader"]!;
        var book = (await WantedAsync(db, reader, cancellationToken)).FirstOrDefault(item => item.Id == id);
        return book is not null && store.OpenPath(book.EbookStoredName) is { } path
            ? Results.File(path, "application/epub+zip", $"{Safe(book.Author)} - {Safe(book.Title)}.epub")
            : TypedResults.NotFound();
    }

    private static async Task<IResult> Cover(Guid uuid, HttpContext http, ShelfDb db, EbookStore store, CoverStore covers, CancellationToken cancellationToken)
    {
        var reader = (Reader)http.Items["kobo-reader"]!;
        var book = (await WantedAsync(db, reader, cancellationToken)).FirstOrDefault(item => UuidOf(item.Id) == uuid);
        return book is null ? TypedResults.NotFound() : Covers.Serve(book.EbookStoredName, book.CoverImage, store, covers, http);
    }

    private static long BookSize(HttpContext http, Book book) =>
        http.RequestServices.GetRequiredService<EbookStore>().OpenPath(book.EbookStoredName) is { } path ? new FileInfo(path).Length : 0;

    private static string Stamp(DateTimeOffset when) => when.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static JsonObject Entitlement(Book book, bool removed)
    {
        var id = UuidOf(book.Id).ToString();
        var added = Stamp(book.AddedAt == default ? DateTimeOffset.UtcNow : book.AddedAt);
        return new JsonObject
        {
            ["Accessibility"] = "Full",
            ["ActivePeriod"] = new JsonObject { ["From"] = added },
            ["Created"] = added,
            ["CrossRevisionId"] = id,
            ["Id"] = id,
            ["IsHiddenFromArchive"] = false,
            ["IsLocked"] = false,
            ["IsRemoved"] = removed,
            ["LastModified"] = Stamp(DateTimeOffset.UtcNow),
            ["OriginCategory"] = "Imported",
            ["RevisionId"] = id,
            ["Status"] = "Active",
        };
    }

    private static JsonObject BookMetadata(Book book, string host, long size)
    {
        var id = UuidOf(book.Id).ToString();
        var metadata = new JsonObject
        {
            ["Categories"] = new JsonArray("00000000-0000-0000-0000-000000000001"),
            ["CoverImageId"] = id,
            ["CrossRevisionId"] = id,
            ["CurrentDisplayPrice"] = new JsonObject { ["CurrencyCode"] = "USD", ["TotalAmount"] = 0 },
            ["CurrentLoveDisplayPrice"] = new JsonObject { ["TotalAmount"] = 0 },
            ["Description"] = book.Subtitle ?? "",
            ["DownloadUrls"] = new JsonArray(new JsonObject { ["Format"] = "EPUB", ["Size"] = size, ["Url"] = $"{host}/download/{book.Id}/epub", ["Platform"] = "Generic" }),
            ["EntitlementId"] = id,
            ["ExternalIds"] = new JsonArray(),
            ["Genre"] = "00000000-0000-0000-0000-000000000001",
            ["IsEligibleForKoboLove"] = false,
            ["IsInternetArchive"] = false,
            ["IsPreOrder"] = false,
            ["IsSocialEnabled"] = true,
            ["Language"] = WordLookups.LanguageOf(book.Language),
            ["PhoneticPronunciations"] = new JsonObject(),
            ["PublicationDate"] = book.Year is int year ? $"{year:D4}-01-01T00:00:00Z" : Stamp(book.AddedAt),
            ["Publisher"] = new JsonObject { ["Imprint"] = "", ["Name"] = book.Publisher ?? "" },
            ["RevisionId"] = id,
            ["Title"] = book.Title,
            ["WorkId"] = id,
            ["ContributorRoles"] = new JsonArray(new JsonObject { ["Name"] = book.Author }),
            ["Contributors"] = new JsonArray(book.Author),
        };
        if (!string.IsNullOrWhiteSpace(book.Series))
        {
            metadata["Series"] = new JsonObject
            {
                ["Name"] = book.Series,
                ["Number"] = book.SeriesNumber ?? 1,
                ["NumberFloat"] = (double)(book.SeriesNumber ?? 1),
                ["Id"] = new Guid(MD5.HashData(Encoding.UTF8.GetBytes("shelf-kobo-series-" + book.Series.ToLowerInvariant()))).ToString(),
            };
        }

        return metadata;
    }

    // The place: the Kobo's own, as it last sent it; or, for a book it has not opened, Shelf's status.
    private static JsonObject ReadingState(Book book, KoboEntry? entry)
    {
        var id = UuidOf(book.Id).ToString();
        var modified = Stamp(entry?.ModifiedAt ?? book.AddedAt);
        var status = entry?.Status ?? book.Status switch
        {
            BookStatus.Finished => "Finished",
            BookStatus.Reading => "Reading",
            _ => "ReadyToRead",
        };
        JsonNode bookmark = entry?.Bookmark is { } kept ? JsonNode.Parse(kept)! : new JsonObject { ["LastModified"] = modified };
        return new JsonObject
        {
            ["EntitlementId"] = id,
            ["Created"] = Stamp(book.AddedAt),
            ["LastModified"] = modified,
            ["PriorityTimestamp"] = modified,
            ["StatusInfo"] = new JsonObject { ["LastModified"] = modified, ["Status"] = status, ["TimesStartedReading"] = status == "ReadyToRead" ? 0 : 1 },
            ["Statistics"] = new JsonObject { ["LastModified"] = modified },
            ["CurrentBookmark"] = bookmark,
        };
    }

    private static string Safe(string text) => new(text.Where(character => !Path.GetInvalidFileNameChars().Contains(character)).ToArray());

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    // The rest of the Kobo store's own addresses, so its store and account pages keep working on the device.
    private static readonly Dictionary<string, string> Store = new(StringComparer.Ordinal)
    {
        ["account_page"] = "https://secure.kobobooks.com/profile",
        ["account_page_rakuten"] = "https://my.rakuten.co.jp/",
        ["add_entitlement"] = "https://storeapi.kobo.com/v1/library/{RevisionIds}",
        ["affiliaterequest"] = "https://storeapi.kobo.com/v1/affiliate",
        ["audiobook_subscription_orange_deal_inclusion_url"] = "https://authorize.kobo.com/inclusion",
        ["authorproduct_recommendations"] = "https://storeapi.kobo.com/v1/products/books/authors/recommendations",
        ["autocomplete"] = "https://storeapi.kobo.com/v1/products/autocomplete",
        ["blackstone_header"] = "{\"key\":\"x-amz-request-payer\",\"value\":\"requester\"}",
        ["book"] = "https://storeapi.kobo.com/v1/products/books/{ProductId}",
        ["book_detail_page"] = "https://store.kobobooks.com/{culture}/ebook/{slug}",
        ["book_detail_page_rakuten"] = "https://books.rakuten.co.jp/rk/{crossrevisionid}",
        ["book_landing_page"] = "https://store.kobobooks.com/ebooks",
        ["book_subscription"] = "https://storeapi.kobo.com/v1/products/books/subscriptions",
        ["categories"] = "https://storeapi.kobo.com/v1/categories",
        ["categories_page"] = "https://store.kobobooks.com/ebooks/categories",
        ["category"] = "https://storeapi.kobo.com/v1/categories/{CategoryId}",
        ["category_featured_lists"] = "https://storeapi.kobo.com/v1/categories/{CategoryId}/featured",
        ["category_products"] = "https://storeapi.kobo.com/v1/categories/{CategoryId}/products",
        ["checkout_borrowed_book"] = "https://storeapi.kobo.com/v1/library/borrow",
        ["configuration_data"] = "https://storeapi.kobo.com/v1/configuration",
        ["content_access_book"] = "https://storeapi.kobo.com/v1/products/books/{ProductId}/access",
        ["customer_care_live_chat"] = "https://v2.zopim.com/widget/livechat.html?key=Y6gwUmnu4OATxN3Tli4Av9bYN319BTdO",
        ["daily_deal"] = "https://storeapi.kobo.com/v1/products/dailydeal",
        ["deals"] = "https://storeapi.kobo.com/v1/deals",
        ["delete_entitlement"] = "https://storeapi.kobo.com/v1/library/{Ids}",
        ["delete_tag"] = "https://storeapi.kobo.com/v1/library/tags/{TagId}",
        ["delete_tag_items"] = "https://storeapi.kobo.com/v1/library/tags/{TagId}/items/delete",
        ["device_refresh"] = "https://storeapi.kobo.com/v1/auth/refresh",
        ["dictionary_host"] = "https://kbdownload1-a.akamaihd.net",
        ["discovery_host"] = "https://discovery.kobobooks.com",
        ["eula_page"] = "https://www.kobo.com/termsofuse?style=onestore",
        ["exchange_auth"] = "https://storeapi.kobo.com/v1/auth/exchange",
        ["external_book"] = "https://storeapi.kobo.com/v1/products/books/external/{Ids}",
        ["featured_list"] = "https://storeapi.kobo.com/v1/products/featured/{FeaturedListId}",
        ["featured_lists"] = "https://storeapi.kobo.com/v1/products/featured",
        ["free_books_page"] = "{\"EN\":\"https://www.kobo.com/{region}/{language}/p/free-ebooks\"}",
        ["fte_feedback"] = "https://storeapi.kobo.com/v1/products/ftefeedback",
        ["get_tests_request"] = "https://storeapi.kobo.com/v1/analytics/gettests",
        ["giftcard_epd_redeem_url"] = "https://www.kobo.com/{storefront}/{language}/redeem-ereader",
        ["giftcard_redeem_url"] = "https://www.kobo.com/{storefront}/{language}/redeem",
        ["help_page"] = "https://www.kobo.com/help",
        ["kobo_audiobooks_enabled"] = "False",
        ["kobo_audiobooks_orange_deal_enabled"] = "False",
        ["kobo_audiobooks_subscriptions_enabled"] = "False",
        ["kobo_nativeborrow_enabled"] = "True",
        ["kobo_onestorelibrary_enabled"] = "False",
        ["kobo_redeem_enabled"] = "True",
        ["kobo_shelfie_enabled"] = "False",
        ["kobo_subscriptions_enabled"] = "False",
        ["kobo_superpoints_enabled"] = "False",
        ["kobo_wishlist_enabled"] = "True",
        ["love_dashboard_page"] = "https://store.kobobooks.com/{culture}/kobosuperpoints",
        ["love_points_redemption_page"] = "https://store.kobobooks.com/{culture}/KoboSuperPointsRedemption?productId={ProductId}",
        ["magazine_landing_page"] = "https://store.kobobooks.com/emagazines",
        ["notifications_registration_issue"] = "https://storeapi.kobo.com/v1/notifications/registration",
        ["oauth_host"] = "https://oauth.kobo.com",
        ["password_retrieval_page"] = "https://www.kobobooks.com/passwordretrieval.html",
        ["post_analytics_event"] = "https://storeapi.kobo.com/v1/analytics/event",
        ["privacy_page"] = "https://www.kobo.com/privacypolicy?style=onestore",
        ["product_nextread"] = "https://storeapi.kobo.com/v1/products/{ProductIds}/nextread",
        ["product_prices"] = "https://storeapi.kobo.com/v1/products/{ProductIds}/prices",
        ["product_recommendations"] = "https://storeapi.kobo.com/v1/products/{ProductId}/recommendations",
        ["product_reviews"] = "https://storeapi.kobo.com/v1/products/{ProductIds}/reviews",
        ["products"] = "https://storeapi.kobo.com/v1/products",
        ["provider_external_sign_in_page"] = "https://authorize.kobo.com/ExternalSignIn/{providerName}?returnUrl=http://kobo.com/",
        ["purchase_buy"] = "https://www.kobo.com/checkout/createpurchase/",
        ["purchase_buy_templated"] = "https://www.kobo.com/{culture}/checkout/createpurchase/{ProductId}",
        ["quickbuy_checkout"] = "https://storeapi.kobo.com/v1/store/quickbuy/{PurchaseId}/checkout",
        ["quickbuy_create"] = "https://storeapi.kobo.com/v1/store/quickbuy/purchase",
        ["rating"] = "https://storeapi.kobo.com/v1/products/{ProductId}/rating/{Rating}",
        ["reading_services_host"] = "https://readingservices.kobo.com",
        ["redeem_interstitial_page"] = "https://store.kobobooks.com",
        ["registration_page"] = "https://authorize.kobo.com/signup?returnUrl=http://kobo.com/",
        ["related_items"] = "https://storeapi.kobo.com/v1/products/{Id}/related",
        ["remaining_book_series"] = "https://storeapi.kobo.com/v1/products/books/series/{SeriesId}",
        ["rename_tag"] = "https://storeapi.kobo.com/v1/library/tags/{TagId}",
        ["review"] = "https://storeapi.kobo.com/v1/products/reviews/{ReviewId}",
        ["review_sentiment"] = "https://storeapi.kobo.com/v1/products/reviews/{ReviewId}/sentiment/{Sentiment}",
        ["shelfie_recommendations"] = "https://storeapi.kobo.com/v1/user/recommendations/shelfie",
        ["sign_in_page"] = "https://authorize.kobo.com/signin?returnUrl=http://kobo.com/",
        ["social_authorization_host"] = "https://social.kobobooks.com:8443",
        ["social_host"] = "https://social.kobobooks.com",
        ["store_home"] = "www.kobo.com/{region}/{language}",
        ["store_host"] = "store.kobobooks.com",
        ["store_newreleases"] = "https://store.kobobooks.com/{culture}/List/new-releases/961XUjtsU0qxkFItWOutGA",
        ["store_search"] = "https://store.kobobooks.com/{culture}/Search?Query={query}",
        ["store_top50"] = "https://store.kobobooks.com/{culture}/ebooks/Top",
        ["tag_items"] = "https://storeapi.kobo.com/v1/library/tags/{TagId}/Items",
        ["tags"] = "https://storeapi.kobo.com/v1/library/tags",
        ["taste_profile"] = "https://storeapi.kobo.com/v1/products/tasteprofile",
        ["update_accessibility_to_preview"] = "https://storeapi.kobo.com/v1/library/{EntitlementIds}/preview",
        ["use_one_store"] = "False",
        ["user_loyalty_benefits"] = "https://storeapi.kobo.com/v1/user/loyalty/benefits",
        ["user_platform"] = "https://storeapi.kobo.com/v1/user/platform",
        ["user_ratings"] = "https://storeapi.kobo.com/v1/user/ratings",
        ["user_recommendations"] = "https://storeapi.kobo.com/v1/user/recommendations",
        ["user_reviews"] = "https://storeapi.kobo.com/v1/user/reviews",
        ["user_wishlist"] = "https://storeapi.kobo.com/v1/user/wishlist",
        ["userguide_host"] = "https://kbdownload1-a.akamaihd.net",
        ["wishlist_page"] = "https://store.kobobooks.com/{region}/{language}/account/wishlist",
    };
}

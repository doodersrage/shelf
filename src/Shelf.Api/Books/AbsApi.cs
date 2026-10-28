using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Books;

// Audiobookshelf's phone apps can sign in to Shelf as to an Audiobookshelf server: they list the reader's audiobooks
// and e-books, play them (streamed or downloaded), and keep the place, bookmarks, and time listened in step with
// Shelf's. Only what the apps use is answered, in the shapes Audiobookshelf 2.37 sends (AbsApi:Enabled).
public sealed class AbsApi(AbsSocket socket, IDataProtectionProvider protection, AudioStore audio, EbookStore ebooks, CoverStore covers, IConfiguration configuration)
{
    private static readonly TimeSpan AccessLife = TimeSpan.FromHours(12);
    private static readonly TimeSpan RefreshLife = TimeSpan.FromDays(30);
    private readonly ConcurrentDictionary<string, (int ReaderId, int BookId, DateTimeOffset StartedAt)> _sessions = new();

    public bool Enabled => configuration.GetValue("AbsApi:Enabled", true);

    private ITimeLimitedDataProtector Access => AccessProtector(protection);

    private static ITimeLimitedDataProtector AccessProtector(IDataProtectionProvider protection) => protection.CreateProtector("Shelf.Abs.Access").ToTimeLimitedDataProtector();

    // The reader an access token was made for, while it lasts, their password is the same, and the API token they signed
    // in with (if they did) is still there; for the live connection too.
    public static async Task<Reader?> ReaderOfAccessAsync(ShelfDb db, IDataProtectionProvider protection, string? token, CancellationToken cancellationToken = default) =>
        (await SessionOfAsync(db, AccessProtector(protection), token, cancellationToken))?.Reader;

    private ITimeLimitedDataProtector Refresh => protection.CreateProtector("Shelf.Abs.Refresh").ToTimeLimitedDataProtector();

    public void Map(WebApplication app)
    {
        if (!Enabled)
        {
            return;
        }

        var open = app.MapGroup("").AllowAnonymous().ExcludeFromDescription().DisableAntiforgery();
        open.MapGet("/ping", () => Results.Json(new { success = true }));
        open.MapGet("/status", () => Results.Json(new JsonObject
        {
            ["app"] = "audiobookshelf",
            ["serverVersion"] = AbsJson.ServerVersion,
            ["isInit"] = true,
            ["language"] = "en-us",
            ["authMethods"] = new JsonArray("local"),
            ["authFormData"] = new JsonObject { ["authLoginCustomMessage"] = "" },
        }));
        open.MapPost("/login", Login);
        open.MapPost("/auth/refresh", RefreshTokens);
        open.MapPost("/logout", () => Results.Json(new JsonObject()));

        var api = app.MapGroup("/api").AllowAnonymous().ExcludeFromDescription().DisableAntiforgery().AddEndpointFilter(Authenticated);
        api.MapPost("/authorize", async (HttpContext http, CancellationToken cancellationToken) => Results.Json(await SignedInAsync(http, Reader(http), null, null, cancellationToken)));
        api.MapGet("/me", async (HttpContext http, CancellationToken cancellationToken) => Results.Json((await SignedInAsync(http, Reader(http), null, null, cancellationToken))["user"]));
        api.MapGet("/libraries", async (ShelfDb db, CancellationToken cancellationToken) =>
            Results.Json(new JsonObject { ["libraries"] = new JsonArray(AbsJson.Library((await BooksAsync(db, cancellationToken)).Count)) }));
        api.MapGet("/libraries/{id}", Library);
        api.MapGet("/libraries/{id}/items", Items);
        api.MapGet("/libraries/{id}/personalized", Personalized);
        api.MapGet("/libraries/{id}/search", SearchLibrary);
        api.MapGet("/libraries/{id}/series", SeriesList);
        api.MapGet("/libraries/{id}/authors", AuthorsList);
        api.MapGet("/libraries/{id}/collections", CollectionsList);
        api.MapGet("/libraries/{id}/playlists", () => Results.Json(new JsonObject { ["results"] = new JsonArray(), ["total"] = 0, ["limit"] = 0, ["page"] = 0 }));
        api.MapGet("/libraries/{id}/recent-episodes", () => Results.Json(new JsonObject { ["episodes"] = new JsonArray(), ["total"] = 0, ["limit"] = 0, ["page"] = 0 }));
        api.MapGet("/series/{id}", SeriesOne);
        api.MapGet("/authors/{id}/image", () => Results.NotFound());
        api.MapGet("/items/{id:int}", Item);
        api.MapGet("/items/{id:int}/cover", Cover);
        api.MapPost("/items/{id:int}/play", Play);
        api.MapGet("/items/{id:int}/file/{ino}", File);
        api.MapGet("/items/{id:int}/file/{ino}/download", File);
        api.MapGet("/items/{id:int}/ebook", Ebook);
        api.MapGet("/items/{id:int}/ebook/{ino}", Ebook);
        api.MapPost("/session/{id}/sync", (string id, JsonObject body, HttpContext http, CancellationToken cancellationToken) => SyncAsync(id, body, http, close: false, cancellationToken));
        api.MapPost("/session/{id}/close", (string id, JsonObject? body, HttpContext http, CancellationToken cancellationToken) => SyncAsync(id, body ?? [], http, close: true, cancellationToken));
        api.MapGet("/session/{id}", (string id) => _sessions.ContainsKey(id) ? Results.Json(new JsonObject { ["id"] = id }) : Results.NotFound());
        api.MapPost("/session/local", LocalSession);
        api.MapPost("/session/local-all", LocalSessions);
        api.MapGet("/me/progress/{id:int}", ProgressOne);
        api.MapPatch("/me/progress/{id:int}", PatchProgress);
        api.MapGet("/me/items-in-progress", InProgress);
        api.MapGet("/me/listening-stats", ListeningStats);
        api.MapPost("/me/item/{id:int}/bookmark", AddBookmark);
        api.MapPatch("/me/item/{id:int}/bookmark", AddBookmark);
        api.MapDelete("/me/item/{id:int}/bookmark/{time}", RemoveBookmark);
    }

    // ---- Signing in ----

    // Who an app is signed in as, and the API token it signed in with, if it gave one rather than a password.
    private sealed record AbsSession(Reader Reader, int? ApiTokenId);

    // A session names the API token it signed in with, if any, so removing the token signs the app out; and the
    // reader's AppStamp, so signing out everywhere else does too. Older sessions, of two or three parts, carry neither.
    private static string Token(ITimeLimitedDataProtector protector, AbsSession session, TimeSpan life) =>
        protector.Protect(
            $"{session.Reader.Id}|{session.Reader.Stamp}|{session.ApiTokenId?.ToString(CultureInfo.InvariantCulture) ?? ""}|{session.Reader.AppStamp ?? ""}", life);

    private static async Task<AbsSession?> SessionOfAsync(ShelfDb db, ITimeLimitedDataProtector protector, string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        string payload;
        try
        {
            payload = protector.Unprotect(token, out _);
        }
        catch (CryptographicException)
        {
            return null;
        }

        var parts = payload.Split('|');
        if (parts.Length is not (2 or 3 or 4) || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var id))
        {
            return null;
        }

        int? apiToken = null;
        if (parts.Length >= 3 && parts[2].Length > 0)
        {
            if (!int.TryParse(parts[2], CultureInfo.InvariantCulture, out var tokenId)
                || !await db.ApiTokens.AsNoTracking().AnyAsync(item => item.Id == tokenId && item.ReaderId == id, cancellationToken))
            {
                return null;
            }

            apiToken = tokenId;
        }

        var reader = await db.Readers.AsNoTracking().FirstOrDefaultAsync(reader => reader.Id == id && reader.Stamp == parts[1], cancellationToken);
        if (reader is null || (parts.Length == 4 && parts[3] != (reader.AppStamp ?? "")))
        {
            return null;
        }

        return new AbsSession(reader, apiToken);
    }

    // Every /api call carries the access token, as a header or, for covers, in the address.
    private async ValueTask<object?> Authenticated(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : http.Request.Query["token"].ToString();
        var db = http.RequestServices.GetRequiredService<ShelfDb>();
        if ((await SessionOfAsync(db, Access, token, http.RequestAborted))?.Reader is not { } reader)
        {
            return Results.Unauthorized();
        }

        http.RequestServices.GetRequiredService<ShelfReader>().Use(reader.Id);
        http.Items["abs-reader"] = reader;
        return await next(context);
    }

    private static Reader Reader(HttpContext http) => (Reader)http.Items["abs-reader"]!;

    public sealed record LoginRequest(string? Username, string? Password);

    // A name and password; a reader with two-step sign-in, or single sign-on only, gives one of their API tokens instead.
    // The app plays, downloads, and keeps the place, so the token must open every part of the shelf and change things:
    // a read-only or narrower one is refused here, rather than let the app in to fail at every play and sync.
    private async Task<IResult> Login(LoginRequest request, HttpContext http, ShelfDb db, CancellationToken cancellationToken)
    {
        Reader? reader = null;
        int? apiToken = null;
        if (request.Password?.StartsWith(ApiTokens.Prefix, StringComparison.Ordinal) == true && await ApiTokens.FindAsync(db, request.Password, cancellationToken) is { } token
            && await ApiTokens.ReaderOfAsync(db, request.Password, cancellationToken) is { } byToken
            && (await ReaderRules.FindAsync(db, request.Username, cancellationToken))?.Id == byToken.Id)
        {
            if (!token.CanChange || token.Areas is { Length: > 0 })
            {
                return Results.Text("This API token can't sign in an Audiobookshelf app: make one that can change things and opens every part of the shelf.", statusCode: StatusCodes.Status401Unauthorized);
            }

            (reader, apiToken) = (byToken, token.Id);
        }
        else if (await ReaderRules.VerifyAsync(db, request.Username, request.Password, cancellationToken) is { TwoFactorEnabled: false } byPassword)
        {
            reader = byPassword;
        }

        if (reader is null || reader.Suspended)
        {
            return Results.Text("Invalid username or password", statusCode: StatusCodes.Status401Unauthorized);
        }

        http.RequestServices.GetRequiredService<ShelfReader>().Use(reader.Id);
        await Audit.NoteAsync(db, Localization.Words.Say("Signed in from an Audiobookshelf app"), reader, cancellationToken: cancellationToken);
        var session = new AbsSession(reader, apiToken);
        return Results.Json(await SignedInAsync(http, reader, Token(Access, session, AccessLife), Token(Refresh, session, RefreshLife), cancellationToken));
    }

    private async Task<IResult> RefreshTokens(HttpContext http, ShelfDb db, CancellationToken cancellationToken)
    {
        if (await SessionOfAsync(db, Refresh, http.Request.Headers["x-refresh-token"].ToString(), cancellationToken) is not { } session)
        {
            return Results.Unauthorized();
        }

        http.RequestServices.GetRequiredService<ShelfReader>().Use(session.Reader.Id);
        return Results.Json(await SignedInAsync(http, session.Reader, Token(Access, session, AccessLife), Token(Refresh, session, RefreshLife), cancellationToken));
    }

    // What signing in answers: the user, with their progress and bookmarks, and the server's settings.
    private async Task<JsonObject> SignedInAsync(HttpContext http, Reader reader, string? access, string? refresh, CancellationToken cancellationToken)
    {
        var db = http.RequestServices.GetRequiredService<ShelfDb>();
        var books = await BooksAsync(db, cancellationToken);
        var progress = new JsonArray();
        var marks = new JsonArray();
        foreach (var book in books)
        {
            var (recording, finished, updated) = await StateAsync(db, book, cancellationToken);
            if (AbsJson.Progress(book, recording, reader.Id, finished, updated) is { } one)
            {
                progress.Add(one);
            }

            if (recording is not null && await Lending.OpenAsync(db, book.Id, cancellationToken) is { } open)
            {
                foreach (var mark in await Lending.Bookmarks(db, open).AsNoTracking().ToListAsync(cancellationToken))
                {
                    marks.Add(AbsJson.Bookmark(book.Id, mark.Note ?? "", recording.Tracks.Take(mark.Track).Sum(track => track.Length) + mark.Seconds, mark.CreatedAt));
                }
            }
        }

        var language = (reader.Language ?? "en") switch { "es" => "es", "fr" => "fr", "de" => "de", _ => "en-us" };
        return new JsonObject
        {
            ["user"] = AbsJson.User(reader, progress, marks, access, refresh),
            ["userDefaultLibraryId"] = AbsJson.LibraryId,
            ["serverSettings"] = AbsJson.ServerSettings(language),
            ["ereaderDevices"] = new JsonArray(),
            ["Source"] = "shelf",
        };
    }

    // ---- The library ----

    // The reader's books with a recording or an EPUB: their own and those lent to them.
    private static async Task<List<Book>> BooksAsync(ShelfDb db, CancellationToken cancellationToken)
    {
        var me = db.ReaderId;
        return await db.Books.IgnoreQueryFilters().AsNoTracking().Include(book => book.Tags)
            .Where(book => (book.OwnerId == me || book.BorrowerId == me) && (book.AudioStoredName != null || (book.EbookStoredName != null && book.EbookStoredName.EndsWith(".epub"))))
            .ToListAsync(cancellationToken);
    }

    private async Task<(AbsJson.Recording? Recording, bool Finished, DateTimeOffset? Updated)> StateAsync(ShelfDb db, Book book, CancellationToken cancellationToken)
    {
        var plan = book.AudioStoredName is null ? null : await AudioPlayback.PlanAsync(db, audio, book.Id, cancellationToken);
        var recording = AbsJson.RecordingOf(plan, audio, book);
        var finished = book.OwnerId == db.ReaderId && book.Status == BookStatus.Finished;
        var day = await db.ListeningDays.IgnoreQueryFilters().AsNoTracking().Where(item => item.BookId == book.Id && item.ReaderId == db.ReaderId).OrderByDescending(item => item.Day).Select(item => (DateOnly?)item.Day).FirstOrDefaultAsync(cancellationToken);
        return (recording, finished, day is DateOnly last ? new DateTimeOffset(last.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero) : null);
    }

    private async Task<JsonObject> MinifiedAsync(ShelfDb db, Book book, bool withProgress, CancellationToken cancellationToken)
    {
        var (recording, finished, updated) = await StateAsync(db, book, cancellationToken);
        var item = AbsJson.ItemMinified(book, recording, ebooks);
        if (withProgress && AbsJson.Progress(book, recording, db.ReaderId, finished, updated) is { } progress)
        {
            item["progressLastUpdate"] = progress["lastUpdate"]!.GetValue<long>();
        }

        return item;
    }

    private async Task<IResult> Library(string id, ShelfDb db, CancellationToken cancellationToken)
    {
        var books = await BooksAsync(db, cancellationToken);
        static JsonArray Named(IEnumerable<string> names, Func<string, string> ids) => new([.. names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(name => (JsonNode)new JsonObject { ["id"] = ids(name), ["name"] = name })]);
        return Results.Json(new JsonObject
        {
            ["library"] = AbsJson.Library(books.Count),
            ["filterdata"] = new JsonObject
            {
                ["authors"] = Named(books.SelectMany(book => AbsJson.Split(book.Author)), AbsJson.AuthorId),
                ["genres"] = new JsonArray([.. books.SelectMany(book => book.Tags.Select(tag => tag.Name)).Distinct().Order().Select(name => (JsonNode)name)]),
                ["tags"] = new JsonArray(),
                ["series"] = Named(books.Select(book => book.Series).OfType<string>(), AbsJson.SeriesId),
                ["narrators"] = new JsonArray([.. books.SelectMany(book => AbsJson.Split(book.Narrator)).Distinct().Order().Select(name => (JsonNode)name)]),
                ["languages"] = new JsonArray([.. books.Select(book => book.Language).OfType<string>().Distinct().Order().Select(name => (JsonNode)name)]),
                ["publishers"] = new JsonArray([.. books.Select(book => book.Publisher).OfType<string>().Distinct().Order().Select(name => (JsonNode)name)]),
                ["publishedDecades"] = new JsonArray(),
                ["bookCount"] = books.Count,
                ["authorCount"] = books.SelectMany(book => AbsJson.Split(book.Author)).Distinct().Count(),
                ["seriesCount"] = books.Select(book => book.Series).OfType<string>().Distinct().Count(),
                ["podcastCount"] = 0,
                ["numIssues"] = 0,
                ["loadedAt"] = AbsJson.Ms(DateTimeOffset.UtcNow),
            },
            ["issues"] = 0,
            ["numUserPlaylists"] = 0,
        });
    }

    // A page of the library, sorted and filtered as the app asks: by author, series, genre, narrator, or progress.
    private async Task<IResult> Items(string id, HttpContext http, ShelfDb db, CancellationToken cancellationToken)
    {
        var query = http.Request.Query;
        var limit = int.TryParse(query["limit"], out var given) && given > 0 ? Math.Min(given, 500) : 0;
        var page = int.TryParse(query["page"], out var number) && number >= 0 ? number : 0;
        var sort = query["sort"].ToString();
        var descending = query["desc"] == "1";
        var filter = query["filter"].ToString();
        var books = await BooksAsync(db, cancellationToken);
        var states = new Dictionary<int, (AbsJson.Recording? Recording, bool Finished, DateTimeOffset? Updated)>();
        foreach (var book in books)
        {
            states[book.Id] = await StateAsync(db, book, cancellationToken);
        }

        IEnumerable<Book> chosen = books;
        if (filter.Split('.', 2) is [var kind, var encoded])
        {
            var value = Decode(encoded);
            chosen = kind switch
            {
                "authors" => chosen.Where(book => AbsJson.Split(book.Author).Any(name => AbsJson.AuthorId(name) == value)),
                "series" => chosen.Where(book => book.Series is { } series && AbsJson.SeriesId(series) == value),
                "genres" => chosen.Where(book => book.Tags.Any(tag => tag.Name == value)),
                "narrators" => chosen.Where(book => AbsJson.Split(book.Narrator).Contains(value)),
                "languages" => chosen.Where(book => book.Language == value),
                "progress" => value switch
                {
                    "finished" => chosen.Where(book => states[book.Id].Finished),
                    "in-progress" => chosen.Where(book => !states[book.Id].Finished && states[book.Id].Recording?.CurrentTime > 0),
                    "not-started" => chosen.Where(book => !states[book.Id].Finished && !(states[book.Id].Recording?.CurrentTime > 0)),
                    "not-finished" => chosen.Where(book => !states[book.Id].Finished),
                    _ => chosen,
                },
                "ebooks" => chosen.Where(book => EbookStore.IsEpub(book.EbookStoredName)),
                _ => chosen,
            };
        }

        chosen = sort switch
        {
            "media.metadata.authorName" or "media.metadata.authorNameLF" => chosen.OrderBy(book => book.Author, StringComparer.CurrentCultureIgnoreCase),
            "addedAt" => chosen.OrderBy(book => book.AddedAt),
            "media.duration" => chosen.OrderBy(book => states[book.Id].Recording?.Duration ?? 0),
            "media.metadata.publishedYear" => chosen.OrderBy(book => book.Year ?? 0),
            "progress" => chosen.OrderBy(book => states[book.Id].Updated ?? DateTimeOffset.MinValue),
            "random" => chosen.OrderBy(_ => Random.Shared.Next()),
            _ => chosen.OrderBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase),
        };
        var ordered = (descending ? chosen.Reverse() : chosen).ToList();
        var shown = limit > 0 ? ordered.Skip(page * limit).Take(limit) : ordered;
        return Results.Json(new JsonObject
        {
            ["results"] = new JsonArray([.. shown.Select(book => (JsonNode)AbsJson.ItemMinified(book, states[book.Id].Recording, ebooks))]),
            ["total"] = ordered.Count,
            ["limit"] = limit,
            ["page"] = page,
            ["offset"] = page * limit,
            ["sortBy"] = sort.Length > 0 ? sort : null,
            ["sortDesc"] = descending,
            ["filterBy"] = filter.Length > 0 ? filter : null,
            ["mediaType"] = "book",
            ["minified"] = query["minified"] == "1",
            ["collapseseries"] = false,
            ["include"] = query["include"].ToString(),
        });
    }

    private static string Decode(string encoded)
    {
        try
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Uri.UnescapeDataString(encoded)));
        }
        catch (FormatException)
        {
            return encoded;
        }
    }

    // The home page's shelves: carry on listening, carry on reading, recently added, and listen again.
    private async Task<IResult> Personalized(string id, ShelfDb db, CancellationToken cancellationToken)
    {
        var books = await BooksAsync(db, cancellationToken);
        var states = new List<(Book Book, AbsJson.Recording? Recording, bool Finished, DateTimeOffset? Updated)>();
        foreach (var book in books)
        {
            var (recording, finished, updated) = await StateAsync(db, book, cancellationToken);
            states.Add((book, recording, finished, updated));
        }

        JsonObject Shelf(string shelfId, string label, string key, IEnumerable<(Book Book, AbsJson.Recording? Recording, bool Finished, DateTimeOffset? Updated)> items)
        {
            var list = items.Take(20).ToList();
            return new JsonObject
            {
                ["id"] = shelfId,
                ["label"] = label,
                ["labelStringKey"] = key,
                ["type"] = "book",
                ["entities"] = new JsonArray([.. list.Select(item =>
                {
                    var json = AbsJson.ItemMinified(item.Book, item.Recording, ebooks);
                    if (AbsJson.Progress(item.Book, item.Recording, db.ReaderId, item.Finished, item.Updated) is { } progress)
                    {
                        json["progressLastUpdate"] = progress["lastUpdate"]!.GetValue<long>();
                    }

                    return (JsonNode)json;
                })]),
                ["total"] = list.Count,
            };
        }

        var shelves = new JsonArray();
        var listening = states.Where(item => !item.Finished && item.Recording?.CurrentTime > 0).OrderByDescending(item => item.Updated ?? item.Book.AddedAt).ToList();
        if (listening.Count > 0)
        {
            shelves.Add(Shelf("continue-listening", "Continue Listening", "LabelContinueListening", listening));
        }

        var added = states.OrderByDescending(item => item.Book.AddedAt).ToList();
        if (added.Count > 0)
        {
            shelves.Add(Shelf("recently-added", "Recently Added", "LabelRecentlyAdded", added));
        }

        var again = states.Where(item => item.Finished).OrderByDescending(item => item.Book.FinishedOn ?? DateOnly.MinValue).ToList();
        if (again.Count > 0)
        {
            shelves.Add(Shelf("listen-again", "Listen Again", "LabelListenAgain", again));
        }

        return Results.Json(shelves);
    }

    private async Task<IResult> SearchLibrary(string id, string? q, ShelfDb db, CancellationToken cancellationToken)
    {
        var term = (q ?? "").Trim();
        var books = term.Length == 0 ? [] : (await BooksAsync(db, cancellationToken))
            .Where(book => book.Title.Contains(term, StringComparison.CurrentCultureIgnoreCase) || book.Author.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                || book.Narrator?.Contains(term, StringComparison.CurrentCultureIgnoreCase) == true || book.Series?.Contains(term, StringComparison.CurrentCultureIgnoreCase) == true)
            .Take(25).ToList();
        var found = new JsonArray();
        foreach (var book in books)
        {
            var (recording, _, _) = await StateAsync(db, book, cancellationToken);
            found.Add(new JsonObject { ["libraryItem"] = AbsJson.ItemExpanded(book, recording, ebooks) });
        }

        var authors = books.SelectMany(book => AbsJson.Split(book.Author)).Where(name => name.Contains(term, StringComparison.CurrentCultureIgnoreCase)).Distinct().Select(name => (JsonNode)AuthorJson(name, books.Count(book => AbsJson.Split(book.Author).Contains(name))));
        var series = books.Select(book => book.Series).OfType<string>().Where(name => name.Contains(term, StringComparison.CurrentCultureIgnoreCase)).Distinct()
            .Select(name => (JsonNode)new JsonObject { ["series"] = new JsonObject { ["id"] = AbsJson.SeriesId(name), ["name"] = name }, ["books"] = new JsonArray() });
        return Results.Json(new JsonObject
        {
            ["book"] = found,
            ["podcast"] = new JsonArray(),
            ["narrators"] = new JsonArray(),
            ["tags"] = new JsonArray(),
            ["genres"] = new JsonArray(),
            ["series"] = new JsonArray([.. series]),
            ["authors"] = new JsonArray([.. authors]),
        });
    }

    private static JsonObject AuthorJson(string name, int books) => new()
    {
        ["id"] = AbsJson.AuthorId(name),
        ["asin"] = null,
        ["name"] = name,
        ["lastFirst"] = name.Split(' ') is { Length: > 1 } parts ? $"{parts[^1]}, {string.Join(' ', parts[..^1])}" : name,
        ["description"] = null,
        ["imagePath"] = null,
        ["libraryId"] = AbsJson.LibraryId,
        ["addedAt"] = 0,
        ["updatedAt"] = 0,
        ["numBooks"] = books,
    };

    private async Task<IResult> AuthorsList(string id, ShelfDb db, CancellationToken cancellationToken)
    {
        var books = await BooksAsync(db, cancellationToken);
        var authors = books.SelectMany(book => AbsJson.Split(book.Author)).GroupBy(name => name, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => (JsonNode)AuthorJson(group.Key, group.Count()));
        return Results.Json(new JsonObject { ["authors"] = new JsonArray([.. authors]) });
    }

    private async Task<JsonObject> SeriesJsonAsync(ShelfDb db, string name, List<Book> books, CancellationToken cancellationToken)
    {
        var items = new JsonArray();
        foreach (var book in books.OrderBy(book => book.SeriesNumber ?? int.MaxValue))
        {
            var item = await MinifiedAsync(db, book, false, cancellationToken);
            item["media"]!["metadata"]!["series"] = new JsonObject { ["id"] = AbsJson.SeriesId(name), ["name"] = name, ["sequence"] = book.SeriesNumber?.ToString(CultureInfo.InvariantCulture) };
            items.Add(item);
        }

        return new JsonObject
        {
            ["id"] = AbsJson.SeriesId(name),
            ["name"] = name,
            ["nameIgnorePrefix"] = name,
            ["description"] = null,
            ["addedAt"] = AbsJson.Ms(books.Min(book => book.AddedAt)),
            ["updatedAt"] = AbsJson.Ms(books.Max(book => book.AddedAt)),
            ["libraryId"] = AbsJson.LibraryId,
            ["books"] = items,
        };
    }

    private async Task<IResult> SeriesList(string id, ShelfDb db, CancellationToken cancellationToken)
    {
        var books = await BooksAsync(db, cancellationToken);
        var results = new JsonArray();
        foreach (var group in books.Where(book => book.Series is not null).GroupBy(book => book.Series!, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            results.Add(await SeriesJsonAsync(db, group.Key, [.. group], cancellationToken));
        }

        return Results.Json(new JsonObject { ["results"] = results, ["total"] = results.Count, ["limit"] = 0, ["page"] = 0, ["sortDesc"] = false, ["minified"] = true, ["include"] = "" });
    }

    private async Task<IResult> SeriesOne(string id, ShelfDb db, CancellationToken cancellationToken)
    {
        var books = (await BooksAsync(db, cancellationToken)).Where(book => book.Series is { } series && AbsJson.SeriesId(series) == id).ToList();
        return books.Count == 0 ? Results.NotFound() : Results.Json(await SeriesJsonAsync(db, books[0].Series!, books, cancellationToken));
    }

    // Shelf's collections, as Audiobookshelf's: those holding books the app can play or read.
    private async Task<IResult> CollectionsList(string id, ShelfDb db, CancellationToken cancellationToken)
    {
        var books = (await BooksAsync(db, cancellationToken)).ToDictionary(book => book.Id);
        var results = new JsonArray();
        foreach (var collection in await db.Collections.AsNoTracking().Include(item => item.Books).OrderBy(item => item.Name).ToListAsync(cancellationToken))
        {
            var items = new JsonArray();
            foreach (var entry in collection.Books.OrderBy(entry => entry.Position))
            {
                if (books.TryGetValue(entry.BookId, out var book))
                {
                    var (recording, _, _) = await StateAsync(db, book, cancellationToken);
                    items.Add(AbsJson.ItemExpanded(book, recording, ebooks));
                }
            }

            if (items.Count > 0)
            {
                results.Add(new JsonObject
                {
                    ["id"] = $"collection-{collection.Id}",
                    ["libraryId"] = AbsJson.LibraryId,
                    ["name"] = collection.Name,
                    ["description"] = collection.Description,
                    ["books"] = items,
                    ["lastUpdate"] = AbsJson.Ms(collection.CreatedAt),
                    ["createdAt"] = AbsJson.Ms(collection.CreatedAt),
                });
            }
        }

        return Results.Json(new JsonObject { ["results"] = results, ["total"] = results.Count, ["limit"] = 0, ["page"] = 0, ["sortDesc"] = false, ["minified"] = false, ["include"] = "" });
    }

    // ---- Items, files, and playing ----

    private async Task<(OpenBook Open, Book Book)?> OpenAsync(ShelfDb db, int id, CancellationToken cancellationToken)
    {
        if (await Lending.OpenAsync(db, id, cancellationToken) is not { } open)
        {
            return null;
        }

        var book = await db.Books.IgnoreQueryFilters().AsNoTracking().Include(item => item.Tags).FirstAsync(item => item.Id == id, cancellationToken);
        return (open, book);
    }

    private async Task<IResult> Item(int id, string? include, ShelfDb db, CancellationToken cancellationToken)
    {
        if (await OpenAsync(db, id, cancellationToken) is not { } found)
        {
            return Results.NotFound();
        }

        var (recording, finished, updated) = await StateAsync(db, found.Book, cancellationToken);
        var item = AbsJson.ItemExpanded(found.Book, recording, ebooks);
        if (include?.Contains("progress", StringComparison.Ordinal) == true)
        {
            item["userMediaProgress"] = AbsJson.Progress(found.Book, recording, db.ReaderId, finished, updated);
        }

        if (include?.Contains("rssfeed", StringComparison.Ordinal) == true)
        {
            item["rssFeed"] = null;
        }

        return Results.Json(item);
    }

    private async Task<IResult> Cover(int id, HttpContext http, ShelfDb db, CancellationToken cancellationToken)
    {
        if (await OpenAsync(db, id, cancellationToken) is not { } found)
        {
            return Results.NotFound();
        }

        return Covers.Serve(found.Book.EbookStoredName, found.Book.CoverImage, ebooks, covers, http);
    }

    private async Task<IResult> File(int id, string ino, ShelfDb db, CancellationToken cancellationToken)
    {
        if (await OpenAsync(db, id, cancellationToken) is not { } found)
        {
            return Results.NotFound();
        }

        if (ino == AbsJson.EbookIno(id))
        {
            return ebooks.OpenPath(found.Book.EbookStoredName) is { } epub ? Results.File(epub, "application/epub+zip", found.Book.EbookFileName ?? "book.epub", enableRangeProcessing: true) : Results.NotFound();
        }

        var prefix = $"{id}t";
        return ino.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(ino[prefix.Length..], CultureInfo.InvariantCulture, out var index) && audio.TrackPath(found.Book.AudioStoredName, index) is { } path
            ? Results.File(path, AudioStore.ContentType(path), Path.GetFileName(path), enableRangeProcessing: true)
            : Results.NotFound();
    }

    private async Task<IResult> Ebook(int id, ShelfDb db, CancellationToken cancellationToken) =>
        await OpenAsync(db, id, cancellationToken) is { } found && ebooks.OpenPath(found.Book.EbookStoredName) is { } epub && EbookStore.IsEpub(found.Book.EbookStoredName)
            ? Results.File(epub, "application/epub+zip", found.Book.EbookFileName ?? "book.epub", enableRangeProcessing: true)
            : Results.NotFound();

    // A playback session: the tracks to play straight from the shelf, the chapters, and where to start.
    private async Task<IResult> Play(int id, JsonObject? body, HttpContext http, ShelfDb db, CancellationToken cancellationToken)
    {
        if (await OpenAsync(db, id, cancellationToken) is not { } found || found.Book.AudioStoredName is null)
        {
            return Results.NotFound();
        }

        var (recording, _, _) = await StateAsync(db, found.Book, cancellationToken);
        if (recording is null || recording.Tracks.Count == 0)
        {
            return Results.NotFound();
        }

        var reader = Reader(http);
        var sessionId = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;
        _sessions[sessionId] = (reader.Id, id, now);
        var device = body?["deviceInfo"] as JsonObject ?? [];
        var book = found.Book;
        return Results.Json(new JsonObject
        {
            ["id"] = sessionId,
            ["userId"] = AbsJson.UserId(reader.Id),
            ["libraryId"] = AbsJson.LibraryId,
            ["libraryItemId"] = id.ToString(CultureInfo.InvariantCulture),
            ["bookId"] = $"media-{id}",
            ["episodeId"] = null,
            ["mediaType"] = "book",
            ["mediaMetadata"] = AbsJson.SessionMetadata(book),
            ["chapters"] = AbsJson.Chapters(recording),
            ["displayTitle"] = book.Title,
            ["displayAuthor"] = book.Author,
            ["coverPath"] = Covers.HasCover(book) ? $"/shelf/{id}/cover" : null,
            ["duration"] = recording.Duration,
            ["playMethod"] = 0,
            ["mediaPlayer"] = body?["mediaPlayer"]?.GetValue<string>() ?? "unknown",
            ["deviceInfo"] = new JsonObject
            {
                ["id"] = device["deviceId"]?.GetValue<string>() ?? sessionId,
                ["userId"] = AbsJson.UserId(reader.Id),
                ["deviceId"] = device["deviceId"]?.GetValue<string>(),
                ["ipAddress"] = http.Connection.RemoteIpAddress?.ToString(),
                ["clientVersion"] = device["clientVersion"]?.ToString(),
                ["manufacturer"] = device["manufacturer"]?.ToString(),
                ["model"] = device["model"]?.ToString(),
                ["sdkVersion"] = device["sdkVersion"]?.ToString(),
                ["clientName"] = device["clientName"]?.ToString(),
                ["deviceName"] = $"{device["manufacturer"]} {device["model"]}".Trim(),
            },
            ["serverVersion"] = AbsJson.ServerVersion,
            ["date"] = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["dayOfWeek"] = now.DayOfWeek.ToString(),
            ["timeListening"] = 0,
            ["startTime"] = recording.CurrentTime,
            ["currentTime"] = recording.CurrentTime,
            ["startedAt"] = AbsJson.Ms(now),
            ["updatedAt"] = AbsJson.Ms(now),
            ["audioTracks"] = new JsonArray([.. Enumerable.Range(0, recording.Tracks.Count).Select(position => (JsonNode)AbsJson.Track(book, recording, position))]),
            ["libraryItem"] = AbsJson.ItemExpanded(book, recording, ebooks),
        });
    }

    // Seconds from the start of the book, kept as Shelf keeps a place: a track and the seconds into it.
    // The place in the recording, when the app gives a time; with none (an app marking a book finished, say), the
    // place stays where it is rather than going back to the start.
    // Sessions the app played offline and sends late are forwardOnly: they move the place on, never back.
    private async Task<bool> KeepAsync(ShelfDb db, int bookId, double? currentTime, double listened, bool finished, CancellationToken cancellationToken, bool forwardOnly = false)
    {
        if (await OpenAsync(db, bookId, cancellationToken) is not { } found)
        {
            return false;
        }

        if (currentTime is double time && !double.IsNaN(time) && found.Book.AudioStoredName is { } stored)
        {
            var lengths = audio.Lengths(stored);
            var (track, before) = (0, 0.0);
            while (track < lengths.Count - 1 && lengths[track] is double length && time >= before + length)
            {
                before += length;
                track++;
            }

            using (Positions.Reporting("Audiobookshelf"))
            {
                var seconds = (int)Math.Max(0, time - before);
                await Lending.KeepPlaceAsync(db, found.Open, place =>
                {
                    if (!forwardOnly || AudioPlayback.Further(place, track, seconds))
                    {
                        (place.AudioTrack, place.AudioSeconds) = (track, seconds);
                    }
                }, cancellationToken);
            }

            if (listened >= 1)
            {
                await AudioPlayback.NoteListeningAsync(db, bookId, null, listened, cancellationToken);
            }
        }
        else if (!finished)
        {
            return false;
        }

        if (finished && !found.Open.Borrowed && await db.Books.FirstOrDefaultAsync(item => item.Id == bookId, cancellationToken) is { Status: not BookStatus.Finished } book)
        {
            BookRules.ChangeStatus(book, BookStatus.Finished);
            await db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    private static double Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;

    private async Task<IResult> SyncAsync(string id, JsonObject body, HttpContext http, bool close, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(id, out var session) || session.ReaderId != Reader(http).Id)
        {
            return Results.NotFound();
        }

        var db = http.RequestServices.GetRequiredService<ShelfDb>();
        if (body.ContainsKey("currentTime"))
        {
            await KeepAsync(db, session.BookId, Number(body["currentTime"]), Number(body["timeListened"]), false, cancellationToken);
        }

        if (close)
        {
            _sessions.TryRemove(id, out _);
        }

        await TellAsync(db, session.ReaderId, session.BookId, id, cancellationToken);
        return Results.Ok();
    }

    // The reader's other apps hear of the new place at once.
    private async Task TellAsync(ShelfDb db, int readerId, int bookId, string? sessionId, CancellationToken cancellationToken)
    {
        if (socket.Connected(readerId) == 0 || await OpenAsync(db, bookId, cancellationToken) is not { } found)
        {
            return;
        }

        var (recording, finished, updated) = await StateAsync(db, found.Book, cancellationToken);
        if (AbsJson.Progress(found.Book, recording, readerId, finished, updated ?? DateTimeOffset.UtcNow) is { } progress)
        {
            await socket.SendAsync(readerId, "user_item_progress_updated", new JsonObject { ["id"] = progress["id"]!.GetValue<string>(), ["sessionId"] = sessionId, ["deviceDescription"] = null, ["data"] = progress }, cancellationToken);
        }
    }

    // Sessions the app played with no connection, sent when it has one again.
    private async Task<IResult> LocalSession(JsonObject body, ShelfDb db, CancellationToken cancellationToken)
    {
        if (!int.TryParse(body["libraryItemId"]?.ToString(), CultureInfo.InvariantCulture, out var bookId))
        {
            return Results.BadRequest();
        }

        await KeepAsync(db, bookId, Number(body["currentTime"]), Number(body["timeListening"]), false, cancellationToken, forwardOnly: true);
        return Results.Ok();
    }

    private async Task<IResult> LocalSessions(JsonObject body, ShelfDb db, CancellationToken cancellationToken)
    {
        var results = new JsonArray();
        foreach (var session in body["sessions"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var synced = int.TryParse(session["libraryItemId"]?.ToString(), CultureInfo.InvariantCulture, out var bookId)
                && await KeepAsync(db, bookId, Number(session["currentTime"]), Number(session["timeListening"]), false, cancellationToken, forwardOnly: true);
            results.Add(new JsonObject { ["id"] = session["id"]?.ToString(), ["success"] = synced, ["progressSynced"] = synced });
        }

        return Results.Json(new JsonObject { ["results"] = results });
    }

    private async Task<IResult> ProgressOne(int id, ShelfDb db, CancellationToken cancellationToken)
    {
        if (await OpenAsync(db, id, cancellationToken) is not { } found)
        {
            return Results.NotFound();
        }

        var (recording, finished, updated) = await StateAsync(db, found.Book, cancellationToken);
        return AbsJson.Progress(found.Book, recording, db.ReaderId, finished, updated) is { } progress ? Results.Json(progress) : Results.NotFound();
    }

    private async Task<IResult> PatchProgress(int id, JsonObject body, HttpContext http, ShelfDb db, CancellationToken cancellationToken)
    {
        var finished = body["isFinished"] is JsonValue value && value.TryGetValue<bool>(out var done) && done;

        // A place in the e-book from the apps' reader: kept to the word, as Shelf's reader keeps it.
        if (body["ebookLocation"]?.ToString() is { Length: > 0 and <= 1000 } location && location.StartsWith("epubcfi(", StringComparison.Ordinal)
            && await Lending.OpenAsync(db, id, cancellationToken) is { } open)
        {
            var share = body["ebookProgress"] is JsonValue given && given.TryGetValue<double>(out var part) ? Math.Clamp(part, 0, 1) : (double?)null;
            using var _ = Positions.Reporting("Audiobookshelf");
            await Lending.KeepPlaceAsync(db, open, place =>
            {
                place.EbookCfi = location;
                place.EbookChapter = AbsClient.SpineIndex(location) ?? place.EbookChapter;
                place.EbookProgress = share ?? place.EbookProgress;
            }, cancellationToken);
        }

        if (body.ContainsKey("currentTime") || finished)
        {
            if (!await KeepAsync(db, id, body.ContainsKey("currentTime") ? Number(body["currentTime"]) : null, 0, finished, cancellationToken))
            {
                return await Lending.OpenAsync(db, id, cancellationToken) is null ? Results.NotFound() : Results.Ok();
            }
        }

        await TellAsync(db, Reader(http).Id, id, null, cancellationToken);
        return Results.Ok();
    }

    private async Task<IResult> InProgress(ShelfDb db, CancellationToken cancellationToken)
    {
        var items = new JsonArray();
        foreach (var book in await BooksAsync(db, cancellationToken))
        {
            var (recording, finished, updated) = await StateAsync(db, book, cancellationToken);
            if (!finished && recording?.CurrentTime > 0)
            {
                var item = AbsJson.ItemMinified(book, recording, ebooks);
                item["progressLastUpdate"] = AbsJson.Ms(updated ?? book.AddedAt);
                items.Add(item);
            }
        }

        return Results.Json(new JsonObject { ["libraryItems"] = items });
    }

    private async Task<IResult> ListeningStats(ShelfDb db, CancellationToken cancellationToken)
    {
        var me = db.ReaderId;
        var days = await db.ListeningDays.IgnoreQueryFilters().AsNoTracking().Where(item => item.ReaderId == me).Include(item => item.Book).ToListAsync(cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var byItem = new JsonObject();
        foreach (var group in days.GroupBy(item => item.BookId))
        {
            var book = group.First().Book!;
            byItem[book.Id.ToString(CultureInfo.InvariantCulture)] = new JsonObject
            {
                ["id"] = book.Id.ToString(CultureInfo.InvariantCulture),
                ["timeListening"] = group.Sum(item => item.Seconds),
                ["mediaMetadata"] = AbsJson.SessionMetadata(book),
            };
        }

        var byDay = new JsonObject();
        foreach (var group in days.GroupBy(item => item.Day))
        {
            byDay[group.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)] = group.Sum(item => item.Seconds);
        }

        var byWeekday = new JsonObject();
        foreach (var group in days.GroupBy(item => item.Day.DayOfWeek))
        {
            byWeekday[group.Key.ToString()] = group.Sum(item => item.Seconds);
        }

        return Results.Json(new JsonObject
        {
            ["totalTime"] = days.Sum(item => item.Seconds),
            ["items"] = byItem,
            ["days"] = byDay,
            ["dayOfWeek"] = byWeekday,
            ["today"] = days.Where(item => item.Day == today).Sum(item => item.Seconds),
            ["recentSessions"] = new JsonArray(),
        });
    }

    // Bookmarks: a time in the whole book, kept as Shelf keeps them, at a track and the seconds into it.
    private async Task<IResult> AddBookmark(int id, JsonObject body, ShelfDb db, CancellationToken cancellationToken)
    {
        if (await OpenAsync(db, id, cancellationToken) is not { } found || found.Book.AudioStoredName is null)
        {
            return Results.NotFound();
        }

        var time = Number(body["time"]);
        var title = body["title"]?.ToString() is { Length: > 0 } given ? given[..Math.Min(given.Length, 500)] : "";
        var lengths = audio.Lengths(found.Book.AudioStoredName);
        var (track, before) = (0, 0.0);
        while (track < lengths.Count - 1 && lengths[track] is double length && time >= before + length)
        {
            before += length;
            track++;
        }

        var seconds = (int)Math.Max(0, time - before);
        var existing = await Lending.Bookmarks(db, found.Open).FirstOrDefaultAsync(mark => mark.Track == track && mark.Seconds == seconds, cancellationToken);
        if (existing is null)
        {
            existing = new AudioBookmark { BookId = id, ReaderId = found.Open.Borrowed ? db.ReaderId : null, Track = track, Seconds = seconds, CreatedAt = DateTimeOffset.UtcNow };
            db.AudioBookmarks.Add(existing);
        }

        existing.Note = title;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Json(AbsJson.Bookmark(id, title, time, existing.CreatedAt));
    }

    private async Task<IResult> RemoveBookmark(int id, string time, ShelfDb db, CancellationToken cancellationToken)
    {
        if (await OpenAsync(db, id, cancellationToken) is not { } found || found.Book.AudioStoredName is null || !double.TryParse(time, CultureInfo.InvariantCulture, out var at))
        {
            return Results.NotFound();
        }

        var lengths = audio.Lengths(found.Book.AudioStoredName);
        foreach (var mark in await Lending.Bookmarks(db, found.Open).ToListAsync(cancellationToken))
        {
            if (Math.Abs(lengths.Take(mark.Track).Sum(length => length ?? 0) + mark.Seconds - at) < 1)
            {
                db.AudioBookmarks.Remove(mark);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok();
    }
}

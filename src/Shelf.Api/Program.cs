using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shelf.Api;
using Shelf.Api.Books;
using Shelf.Api.Components;
using Shelf.Api.Data;
using Shelf.Api.Readers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.AddServiceDefaults();

// Aspire injects ConnectionStrings:sqlite. Direct runs and tests use Shelf.
var configuredConnection = builder.Configuration.GetConnectionString("sqlite")
    ?? builder.Configuration.GetConnectionString("Shelf")
    ?? throw new InvalidOperationException("Connection string 'sqlite' or 'Shelf' is missing.");
var sqlite = new SqliteConnectionStringBuilder(configuredConnection);
if (!Path.IsPathRooted(sqlite.DataSource))
{
    sqlite.DataSource = Path.Combine(builder.Environment.ContentRootPath, sqlite.DataSource);
}

// Sign-in cookies are sealed with these keys. Keeping them beside the database means a redeploy or a
// new container does not sign everyone out, and a snapshot restore brings them back with the data.
var keysPath = builder.Configuration["DataProtection:KeysPath"]
    ?? Path.Combine(Path.GetDirectoryName(sqlite.DataSource) ?? builder.Environment.ContentRootPath, "keys");
builder.Services.AddDataProtection()
    .SetApplicationName("Shelf")
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath));

// Behind a reverse proxy that ends TLS, trust its forwarded scheme and address so HTTPS redirects,
// secure cookies, and the sign-in limit all see the real request.
var behindProxy = builder.Configuration.GetValue("Hosting:BehindProxy", false);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    // The proxy's own addresses, when given: forwarded headers from anywhere else are ignored, so a request that
    // reaches Shelf's port directly can't claim another address to get around the sign-in limit. Without them, any.
    foreach (var item in (builder.Configuration["Hosting:KnownProxies"] ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var range = item.Contains('/') ? item : item + (item.Contains(':') ? "/128" : "/32");
        if (System.Net.IPNetwork.TryParse(range, out var network))
        {
            options.KnownIPNetworks.Add(network);
        }
    }
});

// Scoped, so each context is built in the scope that knows which reader is signed in.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ShelfReader>();
builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<CircuitHandler, ReaderCircuitHandler>());
// SQLite in a file beside the rest, as usual; or PostgreSQL (Database:Provider = Postgres, ConnectionStrings:Postgres),
// with migrations of its own. The SQLite file's folder still holds the keys and backups either way. A book's
// sessions, tags, and the rest come in a query of their own: joined in one, SQLite takes seconds on a shelf of tens
// of thousands what it does apart in a fraction of one.
var onPostgres = builder.Configuration["Database:Provider"] is { } provider && (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase) || provider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase));
if (onPostgres)
{
    var postgres = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("Database:Provider is Postgres, but ConnectionStrings:Postgres is missing.");
    builder.Services.AddDbContextFactory<PostgresShelfDb>(options => options.UseNpgsql(postgres, npgsql => npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)), ServiceLifetime.Scoped);
    builder.Services.AddScoped<IDbContextFactory<ShelfDb>, PostgresShelfDbFactory>();
}
else
{
    builder.Services.AddDbContextFactory<ShelfDb>(options => options.UseSqlite(sqlite.ConnectionString, sqlite => sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)), ServiceLifetime.Scoped);
}

builder.Services.AddScoped(static services =>
    services.GetRequiredService<IDbContextFactory<ShelfDb>>().CreateDbContext());

// Book details: Open Library first, then Google Books for what it does not know (Lookup:GoogleBooks).
builder.Services.AddHttpClient<OpenLibraryLookup>(client =>
{
    client.BaseAddress = new Uri("https://openlibrary.org/");
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Shelf/1.0 (personal library; +https://github.com/doodersrage/shelf)");
});
builder.Services.AddHttpClient<GoogleBooksLookup>(client =>
{
    client.BaseAddress = new Uri("https://www.googleapis.com/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddScoped<IHardcoverLookup, HardcoverLookup>();
builder.Services.AddHttpClient<IBookSearch, OpenLibrarySearch>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Finding:SearchUrl"] is { Length: > 0 } searchUrl ? searchUrl : "https://openlibrary.org/");
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Shelf/1.0 (personal library; +https://github.com/doodersrage/shelf)");
});
builder.Services.AddSingleton<AuthorWatch>();
builder.Services.AddHostedService(static services => services.GetRequiredService<AuthorWatch>());
builder.Services.AddTransient<IBookLookup, CatalogLookup>();
builder.Services.AddTransient<ICatalogSources, CatalogSources>();
// Series alerts: once a day, for readers who ask, new books in their series (SeriesAlerts:Enabled).
builder.Services.AddHttpClient<ISeriesCatalog, OpenLibrarySeries>(client =>
{
    client.BaseAddress = new Uri("https://openlibrary.org/");
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Shelf/1.0 (personal library; +https://github.com/doodersrage/shelf)");
});
builder.Services.AddSingleton<SeriesWatch>();
// Cloudflare Turnstile on the public forms, once an admin gives a widget's keys.
builder.Services.AddHttpClient<ITurnstile, Turnstile>(client => client.Timeout = TimeSpan.FromSeconds(15));
// Highlights sent on to Readwise, for readers who connect it.
builder.Services.AddHttpClient<IReadwiseClient, ReadwiseClient>(client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<ReadwiseSync>();
builder.Services.AddHostedService(static services => services.GetRequiredService<ReadwiseSync>());
// Books' status, stars, and dates sent on to Hardcover, for readers who connect it.
builder.Services.AddHttpClient<IHardcoverClient, HardcoverClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Shelf/1.0 (personal library; +https://github.com/doodersrage/shelf)");
});
builder.Services.AddSingleton<HardcoverSync>();
builder.Services.AddHostedService(static services => services.GetRequiredService<HardcoverSync>());
// Words looked up in the reader, in Wiktionary and Wikipedia (Lookup:Dictionary).
builder.Services.AddHttpClient<IWordLookup, WikimediaLookup>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Shelf/1.0 (personal library; +https://github.com/doodersrage/shelf)");
});
builder.Services.AddHostedService(static services => services.GetRequiredService<SeriesWatch>());
// Comics matched at ComicVine, when an admin gives a key (Comics:ComicVineKey).
builder.Services.AddHttpClient<IComicCatalog, ComicVineCatalog>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Shelf/1.0 (personal library; +https://github.com/doodersrage/shelf)");
});
// Audiobooks matched at Audible for their narrator, series, cover, and chapter names.
builder.Services.AddHttpClient<IAudibleCatalog, AudibleCatalog>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Shelf/1.0 (personal library; +https://github.com/doodersrage/shelf)");
});

// Things that happen, told in the background to notifications and webhooks.
builder.Services.AddSingleton<ShelfEvents>();
builder.Services.AddHostedService(static services => services.GetRequiredService<ShelfEvents>());
// Notifications by browser push, ntfy, or Gotify, for readers who set them up.
builder.Services.AddSingleton(services => new WebPushKeys(keysPath, services.GetRequiredService<IDataProtectionProvider>()));
// Notices and webhooks may go to the local network (ntfy, Home Assistant), never to a cloud's metadata service.
builder.Services.AddHttpClient<INoticeChannels, NoticeChannels>(client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => OutboundGuard.Handler(OutboundReach.Local));
builder.Services.AddSingleton<Notices>();
builder.Services.AddSingleton<IShelfEventHandler>(static services => services.GetRequiredService<Notices>());
builder.Services.AddHostedService(static services => services.GetRequiredService<Notices>());
// Each reader's own webhooks, for Home Assistant, Discord, Slack, and the like (Webhooks:Enabled).
builder.Services.AddHttpClient<IWebhookPoster, WebhookPoster>(client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => OutboundGuard.Handler(OutboundReach.Local));
builder.Services.AddSingleton<Webhooks>();
builder.Services.AddSingleton<IShelfEventHandler>(static services => services.GetRequiredService<Webhooks>());
builder.Services.AddSingleton<AutomationRunner>();
builder.Services.AddSingleton<IShelfEventHandler>(static services => services.GetRequiredService<AutomationRunner>());
builder.Services.AddHostedService(static services => services.GetRequiredService<AutomationRunner>());
builder.Services.AddSingleton<Legacy>();
builder.Services.AddSingleton<ShelfHealth>();
builder.Services.AddHttpClient(nameof(ReleaseWatch), client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd($"Shelf/{ShelfVersion.Response.Version} (update check; +https://github.com/doodersrage/shelf)");
});
builder.Services.AddSingleton<ReleaseWatch>();
builder.Services.AddHostedService(static services => services.GetRequiredService<ReleaseWatch>());
Money.Use(builder.Configuration["Shelf:Currency"]);
// Read-along: a recording heard by a speech-to-text service (ReadAlong:Url) and matched to its e-book, word by word.
// Hearing a long track can take most of an hour, and an upload cannot simply be sent again, so it gets no retries.
#pragma warning disable EXTEXP0001
builder.Services.AddHttpClient<ITranscriber, OpenAiTranscriber>(client => client.Timeout = TimeSpan.FromHours(2)).RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
builder.Services.AddSingleton<IAudioCutter, FfmpegCutter>();
// "Previously in this book", written by a model an admin chooses (Recap:ApiKey); a request is never sent twice.
#pragma warning disable EXTEXP0001
builder.Services.AddHttpClient<IRecapWriter, AnthropicWriter>(client => client.Timeout = TimeSpan.FromMinutes(2)).RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
builder.Services.AddSingleton<Recaps>();
// One M4B from many tracks, by ffmpeg.
builder.Services.AddSingleton<M4bService>();
builder.Services.AddHostedService(static services => services.GetRequiredService<M4bService>());
// Podcasts: feeds read every hour, and the newest episodes kept downloaded.
builder.Services.AddHttpClient<IPodcastClient, PodcastClient>(client =>
{
    client.Timeout = TimeSpan.FromHours(1);
    client.DefaultRequestHeaders.UserAgent.ParseAdd($"Shelf/{ShelfVersion.Response.Version} (podcasts; +https://github.com/doodersrage/shelf)");
}).ConfigurePrimaryHttpMessageHandler(() => OutboundGuard.Handler(OutboundGuard.ReaderReach(builder.Configuration)))
    .Patient(attempt: TimeSpan.FromMinutes(30), total: TimeSpan.FromHours(1));
#pragma warning disable EXTEXP0001
// A feed names its episodes' addresses, and any reader can follow a feed: both kept to the open internet.
builder.Services.AddHttpClient(PodcastClient.StreamName, client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => OutboundGuard.Handler(OutboundGuard.ReaderReach(builder.Configuration))).RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
// Linked shelves: other Shelf servers whose readers borrow from this one's, and lend to them.
#pragma warning disable EXTEXP0001
builder.Services.AddHttpClient(LinkClient.ClientName, client =>
{
    client.Timeout = TimeSpan.FromHours(1);
    client.DefaultRequestHeaders.UserAgent.ParseAdd($"Shelf/{ShelfVersion.Response.Version} (linked shelf)");
}).RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
builder.Services.AddSingleton<LinkClient>();
builder.Services.AddSingleton<Federation>();
builder.Services.AddHostedService(static services => services.GetRequiredService<Federation>());
// Audiobookshelf's phone apps, signing in to Shelf as to an Audiobookshelf server (AbsApi:Enabled).
builder.Services.AddSingleton<AbsSocket>();
builder.Services.AddSingleton<AbsApi>();
builder.Services.AddSingleton<PodcastService>();
builder.Services.AddHostedService(static services => services.GetRequiredService<PodcastService>());
builder.Services.AddSingleton<ReadAlongService>();
builder.Services.AddHostedService(static services => services.GetRequiredService<ReadAlongService>());

builder.Services.AddHttpClient("shelf-sync", client => client.Timeout = TimeSpan.FromMinutes(10));

// Free books keeps a book's details for a while, so opening them twice asks the catalog once.
builder.Services.AddMemoryCache();
// Project Gutenberg and LibriVox, for free public-domain books. A long recording can take a while to come down.
// Gutenberg in particular can take most of a minute to answer, so these get longer than the standard ten seconds an attempt.
builder.Services.AddHttpClient(FreeCatalog.ClientName, client =>
{
    client.Timeout = TimeSpan.FromMinutes(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd($"Shelf/{ShelfVersion.Response.Version} (personal library; +https://github.com/doodersrage/shelf)");
}).Patient(attempt: TimeSpan.FromSeconds(45), total: TimeSpan.FromMinutes(2));
builder.Services.AddSingleton<FreeBooks>();

// Audiobookshelf, to bring a library across. Its address is the reader's to give.
// A large library takes a while to list.
builder.Services.AddHttpClient(AbsClient.ClientName, client => client.Timeout = TimeSpan.FromMinutes(30))
    .Patient(attempt: TimeSpan.FromMinutes(2), total: TimeSpan.FromMinutes(5));
builder.Services.AddHttpClient(AbsClient.ReaderClientName, client => client.Timeout = TimeSpan.FromMinutes(30))
    .ConfigurePrimaryHttpMessageHandler(() => OutboundGuard.Handler(OutboundGuard.ReaderReach(builder.Configuration)))
    .Patient(attempt: TimeSpan.FromMinutes(2), total: TimeSpan.FromMinutes(5));
builder.Services.AddSingleton<ShelfJobs>();
builder.Services.AddSingleton<AbsImporter>();
builder.Services.AddHostedService(static services => services.GetRequiredService<AbsImporter>());
builder.Services.AddHostedService(static services => services.GetRequiredService<FreeBooks>());
builder.Services.AddSingleton<EbookStore>();
builder.Services.AddSingleton<KepubStore>();
builder.Services.AddSingleton<Storage>();
builder.Services.AddSingleton<AudioStore>();
builder.Services.AddSingleton<CoverStore>();
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");
// Copies of each backup off the servers disk: Backup:CopyTo and Backup:S3:*.
builder.Services.AddSingleton<OffsiteBackup>();
builder.Services.AddSingleton<BackupSchedule>();
builder.Services.AddHostedService(static services => services.GetRequiredService<BackupSchedule>());
builder.Services.AddSingleton<BackupCheck>();
builder.Services.AddSingleton<Sandbox>();
builder.Services.AddSingleton<OcrTools>();
builder.Services.AddHostedService<FileSweep>();
// Every stored file read through once a week and checked against what it was (FileCheck:Days).
builder.Services.AddSingleton<FileChecker>();
builder.Services.AddHostedService(static services => services.GetRequiredService<FileChecker>());
builder.Services.AddSingleton<BookImport>();
// Calibre libraries, read from the server's disk by an admin.
builder.Services.AddSingleton<CalibreImporter>();
builder.Services.AddHostedService(static services => services.GetRequiredService<CalibreImporter>());
builder.Services.AddSingleton<AbsKeeper>();
builder.Services.AddHostedService(static services => services.GetRequiredService<AbsKeeper>());
builder.Services.AddSingleton<IAvailability, CatalogAvailability>();
// Finding and fetching books: the indexers and the download clients are on the admin's own network, so redirects
// to a magnet link come back to Shelf rather than being followed.
builder.Services.AddHttpClient(ProwlarrClient.ClientName, client => client.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton<Fetcher>();
builder.Services.AddHostedService(static services => services.GetRequiredService<Fetcher>());
builder.Services.AddSingleton<DetailsFiller>();
builder.Services.AddHostedService(static services => services.GetRequiredService<DetailsFiller>());
// A folder to drop books into (Import:Folder); off unless it is set.
builder.Services.AddSingleton<FolderImport>();
builder.Services.AddHostedService(static services => services.GetRequiredService<FolderImport>());
builder.Services.AddSingleton<OcrService>();
builder.Services.AddHostedService(static services => services.GetRequiredService<OcrService>());
// How large a file may be, in megabytes: an audiobook (1024 unless set) and an e-book (80 unless set). A reverse
// proxy in front of Shelf has a limit of its own to raise too (client_max_body_size in nginx).
AudioStore.SetLimit(builder.Configuration.GetValue<int?>("Uploads:MaxAudioMegabytes"));
EbookStore.SetLimit(builder.Configuration.GetValue<int?>("Uploads:MaxEbookMegabytes"));
var uploadLimit = Math.Max(EbookStore.MaxBytes, AudioStore.MaxBytes);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = uploadLimit;
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = uploadLimit);

// Requests carrying an API token are signed in by it; everything else by the sign-in cookie.
var authentication = builder.Services.AddAuthentication("Shelf")
    .AddPolicyScheme("Shelf", null, options => options.ForwardDefaultSelector = context =>
        context.Request.Headers.Authorization.ToString() is var header
        && header.StartsWith("Bearer " + ApiTokens.Prefix, StringComparison.OrdinalIgnoreCase)
            ? ApiTokens.SchemeName
            : CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/signin";
        options.LogoutPath = "/account/signout";
        options.AccessDeniedPath = "/signin";
        options.Cookie.Name = "shelf";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = EndStaleSession;
        options.Events.OnRedirectToLogin = context => Refuse(context, StatusCodes.Status401Unauthorized);
        options.Events.OnRedirectToAccessDenied = context => Refuse(context, StatusCodes.Status403Forbidden);
    })
    .AddScheme<AuthenticationSchemeOptions, ShelfKeyHandler>(ShelfKeyHandler.SchemeName, null)
    .AddScheme<AuthenticationSchemeOptions, ApiTokenHandler>(ApiTokens.SchemeName, null);
// Single sign-on through an OpenID Connect provider, when Oidc:Authority and Oidc:ClientId are set.
SingleSignOn.Add(authentication, builder.Configuration);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(SyncEndpoints.Policy, policy => policy
        .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme, ShelfKeyHandler.SchemeName)
        .RequireAuthenticatedUser());
builder.Services.AddCascadingAuthenticationState();
var signInsPerMinute = builder.Configuration.GetValue("Accounts:SignInsPerMinute", 10);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AccountEndpoints.SignInLimit, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = signInsPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddValidation();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Run once with --move-to-postgres true, with Database:Provider set to Postgres, to bring the SQLite shelf across.
if (app.Configuration["move-to-postgres"] is { Length: > 0 } && app.Configuration.GetConnectionString("Postgres") is { } target)
{
    var moved = await MoveToPostgres.RunAsync(sqlite.ConnectionString, target);
    Console.WriteLine(moved.Problem ?? $"Moved {moved.Rows} rows in {moved.Tables} tables to PostgreSQL. Start Shelf with Database:Provider set to Postgres.");
    return moved.Problem is null ? 0 : 1;
}

// A shelf moved to a folder Shelf cannot use says so plainly, rather than with SQLite's "unable to open database file".
if (!onPostgres && DataFolder.Problem(sqlite.DataSource) is { } folderProblem)
{
    Console.Error.WriteLine($"Shelf cannot open its database. {folderProblem}");
    return 1;
}

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
    await db.Database.MigrateAsync();

    await ReaderRules.AdoptEmailsAsync(db);

    // Run with --reset-password <email or name> when nobody can sign in to reset it from the admin page.
    if (app.Configuration["reset-password"] is { Length: > 0 } resetName)
    {
        var reader = await ReaderRules.FindAsync(db, resetName);
        var password = reader is null ? null : await ReaderRules.ResetPasswordAsync(db, reader.Id);
        Console.WriteLine(password is null ? $"No reader signs in as {resetName}." : $"The new password for {reader!.Name} is {password}");
        return password is null ? 1 : 0;
    }

    if (app.Configuration["make-admin"] is { Length: > 0 } adminName)
    {
        var reader = await ReaderRules.FindAsync(db, adminName);
        if (reader is not null)
        {
            await ReaderRules.SetAdminAsync(db, reader.Id, true);
        }

        Console.WriteLine(reader is null ? $"No reader signs in as {adminName}." : $"{reader.Name} is an admin.");
        return reader is null ? 1 : 0;
    }

    // The sample book has no owner yet; the first reader to sign up takes it with the rest of the shelf.
    if (app.Environment.IsDevelopment() && !await db.Books.IgnoreQueryFilters().AnyAsync())
    {
        db.Books.Add(new Book
        {
            Title = "The Left Hand of Darkness",
            Author = "Ursula K. Le Guin",
            Status = BookStatus.Want,
            Year = 1969,
            Pages = 304,
            Publisher = "Ace Books",
            Language = "English",
            AddedAt = DateTimeOffset.UtcNow,
            Tags = { new Tag { Name = "science fiction" } },
        });
        await db.SaveChangesAsync();
    }
}

if (behindProxy)
{
    app.UseForwardedHeaders();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// Passwords and device keys travel with every request, so plain HTTP is sent to HTTPS when the shelf has a port for it.
app.UseHttpsRedirection();

// Headers every answer carries: a file's type is never guessed from what is in it; Shelf's pages are never shown inside
// another site's frame, where a click on them could be steered; and a page's address isn't passed to the sites its
// covers come from. The public page and its widget are made to be shown on other sites, so they may be framed.
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "same-origin";
        if (!context.Request.Path.StartsWithSegments("/public"))
        {
            headers.XFrameOptions = "SAMEORIGIN";
            if (!headers.ContainsKey("Content-Security-Policy"))
            {
                headers.ContentSecurityPolicy = "frame-ancestors 'self'";
            }
        }

        return Task.CompletedTask;
    });
    await next();
});

// The library moved from / to /library in 2.0, and / became Home; an address kept from before still finds its view.
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/" && context.Request.QueryString.HasValue && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
    {
        context.Response.Redirect("/library" + context.Request.QueryString, permanent: true);
        return;
    }

    await next();
});
// A browser asking for a page that isn't there is shown Shelf's own, with somewhere to go; the API, and anything else
// not asking for a page, keeps its plain 404 and every other answer as it was.
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.Use(async (context, next) =>
{
    if (context.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } pages)
    {
        var endpoint = context.GetEndpoint();
        pages.Enabled = HttpMethods.IsGet(context.Request.Method)
            && context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase)
            && (endpoint is null || endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Components.Endpoints.ComponentTypeMetadata>() is not null);
    }

    await next();
});
app.UseWebSockets();
app.UseAuthentication();
app.UseRequestLocalization(Regions.Configure);
app.UseRateLimiter();
app.UseAuthorization();
app.UseAntiforgery();
app.Use(ApiTokens.Guard);
app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapAccounts();
app.MapAdmin();
Sandbox.Map(app);
app.MapOpds();
Kosync.Map(app);
app.MapGet("/version", () => TypedResults.Ok(ShelfVersion.Response)).WithTags("Shelf").AllowAnonymous();
app.MapBooks();
SharedLists.Map(app);
Feeds.Map(app);
ReleaseWatch.Map(app);
Kobo.Map(app);
PodcastService.Map(app);
app.Services.GetRequiredService<Federation>().Map(app);
if (app.Services.GetRequiredService<AbsApi>() is { Enabled: true } absApi)
{
    absApi.Map(app);
    app.Services.GetRequiredService<AbsSocket>().Map(app);
}
await app.RunAsync();
return 0;

// A reset or changed password, or a removed reader, ends every session signed in before it.
static async Task EndStaleSession(CookieValidatePrincipalContext context)
{
    var id = ShelfReader.IdOf(context.Principal);
    var stamp = context.Principal?.FindFirst(ReaderRules.StampClaim)?.Value;
    var session = context.Principal?.FindFirst(TwoFactor.SessionClaim)?.Value;
    var db = context.HttpContext.RequestServices.GetRequiredService<ShelfDb>();
    if (id is null
        || !await ReaderRules.StampMatchesAsync(db, id.Value, stamp, context.HttpContext.RequestAborted)
        || !await TwoFactor.TouchSessionAsync(db, id.Value, session, context.HttpContext.RequestAborted))
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}

// Pages send a signed-out visitor to the sign-in page; the JSON API answers with a status instead.
static Task Refuse(RedirectContext<CookieAuthenticationOptions> context, int status)
{
    var path = context.Request.Path;
    if (path.StartsWithSegments("/books") || path.StartsWithSegments("/settings") || path.StartsWithSegments("/readers")
        || path.StartsWithSegments("/admin/readers") || path.StartsWithSegments("/admin/snapshot") || path.StartsWithSegments("/admin/backups") || path.StartsWithSegments("/admin/audit") || path.StartsWithSegments("/account/remove")
        || path.StartsWithSegments("/admin/accounts") || path.StartsWithSegments("/admin/invitations") || path.StartsWithSegments("/admin/signup")
        || path.StartsWithSegments("/admin/announcement") || path.StartsWithSegments("/admin/jobs")
        || path.StartsWithSegments("/opds"))
    {
        context.Response.StatusCode = status;
        return Task.CompletedTask;
    }

    context.Response.Redirect(context.RedirectUri);
    return Task.CompletedTask;
}

public partial class Program { }

static class SlowServers
{
    // Every client gets the standard retries and circuit breaker from ServiceDefaults, ten seconds an attempt, under one
    // shared set of options. This swaps a client's for ones with room for a slow server; the breaker must watch at least
    // two attempts' worth. RemoveAllResilienceHandlers is the library's way to do it, marked experimental only as to its shape.
    public static IHttpClientBuilder Patient(this IHttpClientBuilder client, TimeSpan attempt, TimeSpan total)
    {
#pragma warning disable EXTEXP0001
        client.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        client.AddStandardResilienceHandler(options =>
        {
            options.AttemptTimeout.Timeout = attempt;
            options.TotalRequestTimeout.Timeout = total;
            options.CircuitBreaker.SamplingDuration = attempt * 2;
            // An address the outbound guard refused is refused at once, and doesn't count against the other end.
            var retry = options.Retry.ShouldHandle;
            options.Retry.ShouldHandle = args => OutboundGuard.IsRefusal(args.Outcome.Exception) ? ValueTask.FromResult(false) : retry(args);
            var breaker = options.CircuitBreaker.ShouldHandle;
            options.CircuitBreaker.ShouldHandle = args => OutboundGuard.IsRefusal(args.Outcome.Exception) ? ValueTask.FromResult(false) : breaker(args);
        });
        return client;
    }
}

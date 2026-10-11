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
});

// Scoped, so each context is built in the scope that knows which reader is signed in.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ShelfReader>();
builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<CircuitHandler, ReaderCircuitHandler>());
builder.Services.AddDbContextFactory<ShelfDb>(options => options.UseSqlite(sqlite.ConnectionString), ServiceLifetime.Scoped);
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
builder.Services.AddTransient<IBookLookup, CatalogLookup>();
// Series alerts: once a day, for readers who ask, new books in their series (SeriesAlerts:Enabled).
builder.Services.AddHttpClient<ISeriesCatalog, OpenLibrarySeries>(client =>
{
    client.BaseAddress = new Uri("https://openlibrary.org/");
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Shelf/1.0 (personal library; +https://github.com/doodersrage/shelf)");
});
builder.Services.AddSingleton<SeriesWatch>();
// Words looked up in the reader, in Wiktionary and Wikipedia (Lookup:Dictionary).
builder.Services.AddHttpClient<IWordLookup, WikimediaLookup>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Shelf/1.0 (personal library; +https://github.com/doodersrage/shelf)");
});
builder.Services.AddHostedService(static services => services.GetRequiredService<SeriesWatch>());

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
builder.Services.AddSingleton<AbsImporter>();
builder.Services.AddHostedService(static services => services.GetRequiredService<AbsImporter>());
builder.Services.AddHostedService(static services => services.GetRequiredService<FreeBooks>());
builder.Services.AddSingleton<EbookStore>();
builder.Services.AddSingleton<AudioStore>();
builder.Services.AddSingleton<CoverStore>();
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddSingleton<ReminderMailer>();
builder.Services.AddHostedService(static services => services.GetRequiredService<ReminderMailer>());
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");
// Copies of each backup off the servers disk: Backup:CopyTo and Backup:S3:*.
builder.Services.AddSingleton<OffsiteBackup>();
builder.Services.AddSingleton<BackupSchedule>();
builder.Services.AddHostedService(static services => services.GetRequiredService<BackupSchedule>());
builder.Services.AddSingleton<OcrTools>();
builder.Services.AddHostedService<FileSweep>();
builder.Services.AddSingleton<BookImport>();
// Calibre libraries, read from the server's disk by an admin.
builder.Services.AddSingleton<CalibreImporter>();
builder.Services.AddHostedService(static services => services.GetRequiredService<CalibreImporter>());
// A folder to drop books into (Import:Folder); off unless it is set.
builder.Services.AddSingleton<FolderImport>();
builder.Services.AddHostedService(static services => services.GetRequiredService<FolderImport>());
builder.Services.AddSingleton<OcrService>();
builder.Services.AddHostedService(static services => services.GetRequiredService<OcrService>());
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

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
    await db.Database.MigrateAsync();

    // Run with --reset-password <name> when nobody can sign in to reset it from the admin page.
    if (app.Configuration["reset-password"] is { Length: > 0 } resetName)
    {
        var reader = await db.Readers.FirstOrDefaultAsync(item => item.NormalizedName == ReaderRules.Normalize(resetName));
        var password = reader is null ? null : await ReaderRules.ResetPasswordAsync(db, reader.Id);
        Console.WriteLine(password is null ? $"No reader is named {resetName}." : $"The new password for {reader!.Name} is {password}");
        return password is null ? 1 : 0;
    }

    if (app.Configuration["make-admin"] is { Length: > 0 } adminName)
    {
        var reader = await db.Readers.FirstOrDefaultAsync(item => item.NormalizedName == ReaderRules.Normalize(adminName));
        if (reader is not null)
        {
            await ReaderRules.SetAdminAsync(db, reader.Id, true);
        }

        Console.WriteLine(reader is null ? $"No reader is named {adminName}." : $"{reader.Name} is an admin.");
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
app.MapOpds();
Kosync.Map(app);
app.MapGet("/version", () => TypedResults.Ok(ShelfVersion.Response)).WithTags("Shelf").AllowAnonymous();
app.MapBooks();
SharedLists.Map(app);
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
        });
        return client;
    }
}

using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

// Scoped, so each context is built in the scope that knows which reader is signed in.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ShelfReader>();
builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<CircuitHandler, ReaderCircuitHandler>());
builder.Services.AddDbContextFactory<ShelfDb>(options => options.UseSqlite(sqlite.ConnectionString), ServiceLifetime.Scoped);
builder.Services.AddScoped(static services =>
    services.GetRequiredService<IDbContextFactory<ShelfDb>>().CreateDbContext());

builder.Services.AddHttpClient<IBookLookup, OpenLibraryLookup>(client =>
{
    client.BaseAddress = new Uri("https://openlibrary.org/");
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Shelf/1.0 (personal library; +https://github.com/doodersrage/shelf)");
});

builder.Services.AddHttpClient("shelf-sync", client => client.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddSingleton<EbookStore>();
builder.Services.AddSingleton<AudioStore>();
var uploadLimit = Math.Max(EbookStore.MaxBytes, AudioStore.MaxBytes);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = uploadLimit;
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = uploadLimit);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
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
        options.Events.OnRedirectToLogin = context => Refuse(context, StatusCodes.Status401Unauthorized);
        options.Events.OnRedirectToAccessDenied = context => Refuse(context, StatusCodes.Status403Forbidden);
    })
    .AddScheme<AuthenticationSchemeOptions, ShelfKeyHandler>(ShelfKeyHandler.SchemeName, null);
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

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.UseAntiforgery();
app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapAccounts();
app.MapBooks();
app.Run();

// Pages send a signed-out visitor to the sign-in page; the JSON API answers with a status instead.
static Task Refuse(RedirectContext<CookieAuthenticationOptions> context, int status)
{
    var path = context.Request.Path;
    if (path.StartsWithSegments("/books") || path.StartsWithSegments("/settings") || path.StartsWithSegments("/readers"))
    {
        context.Response.StatusCode = status;
        return Task.CompletedTask;
    }

    context.Response.Redirect(context.RedirectUri);
    return Task.CompletedTask;
}

public partial class Program { }

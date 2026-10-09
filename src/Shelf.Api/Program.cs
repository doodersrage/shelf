using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Books;
using Shelf.Api.Components;
using Shelf.Api.Data;

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

builder.Services.AddDbContextFactory<ShelfDb>(options => options.UseSqlite(sqlite.ConnectionString));
builder.Services.AddScoped(static services =>
    services.GetRequiredService<IDbContextFactory<ShelfDb>>().CreateDbContext());

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

    if (app.Environment.IsDevelopment() && !await db.Books.AnyAsync())
    {
        db.Books.Add(new Book
        {
            Title = "The Left Hand of Darkness",
            Author = "Ursula K. Le Guin",
            Status = BookStatus.Want,
            Year = 1969,
            Pages = 304,
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

app.UseAntiforgery();
app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapBooks();
app.Run();

public partial class Program { }

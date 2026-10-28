using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

// A shelf with data, from the version before, brought up to this one: backed up first, and nothing lost.
public sealed class UpgradeTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task A_shelf_from_the_version_before_is_backed_up_then_upgraded_with_its_books()
    {
        // The shelf's own SQLite file, rolled back by EF's migrations; PostgreSQL's backup is pg_dump's, tested apart.
        if (ShelfApiFactory.OnPostgres)
        {
            return;
        }

        var folder = Directory.CreateTempSubdirectory("shelf-upgrade-").FullName;
        var database = Path.Combine(folder, "shelf.db");
        var backups = Path.Combine(folder, "backups");
        WebApplicationFactory<Program> Shelf(bool backUp = true) => factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Shelf", $"Data Source={database};Pooling=False");
            builder.UseSetting("Backup:Folder", backups);
            builder.UseSetting("Backup:BeforeMigrating", backUp ? "true" : "false");
        });

        try
        {
            // A new shelf, with a reader and a book; being new, it needs no backup to start.
            await using (var app = Shelf())
            {
                var reader = app.CreateClient();
                await ShelfApiFactory.PostFormAsync(reader, "/signup", "/account/signup", new() { ["name"] = "Upgrading Reader", ["email"] = ShelfApiFactory.EmailOf("Upgrading Reader"), ["password"] = ShelfApiFactory.Password, ["confirm"] = ShelfApiFactory.Password });
                (await reader.PostAsJsonAsync("/books", new CreateBookRequest("The Long Upgrade", "Someone", BookStatus.Reading, null), JsonOptions)).EnsureSuccessStatusCode();
            }

            Assert.Empty(Backups(backups));
            var before = await RollBackOneAsync(database);

            // Started on this version: the database is copied as it was, then brought up to date, its book kept.
            await using (var app = Shelf())
            {
                var reader = app.CreateClient();
                await ShelfApiFactory.PostFormAsync(reader, "/signin", "/account/signin", new() { ["name"] = "Upgrading Reader", ["password"] = ShelfApiFactory.Password });
                var books = await reader.GetFromJsonAsync<List<BookResponse>>("/books", JsonOptions);
                Assert.Equal("The Long Upgrade", Assert.Single(books!).Title);
            }

            var backup = Assert.Single(Backups(backups));
            Assert.EndsWith("-before-upgrade.zip", backup, StringComparison.Ordinal);
            var copy = Path.Combine(folder, "copy.db");
            using (var zip = ZipFile.OpenRead(backup))
            {
                zip.GetEntry("shelf.db")!.ExtractToFile(copy);
            }

            await using (var kept = Open(copy))
            {
                Assert.Equal(before, (await kept.Database.GetAppliedMigrationsAsync()).Last());
                Assert.Equal(1, await kept.Books.IgnoreQueryFilters().CountAsync());
            }

            // Asked not to, it upgrades without one.
            await RollBackOneAsync(database);
            await using (var app = Shelf(backUp: false))
            {
                app.CreateClient();
            }

            Assert.Single(Backups(backups));

            // A backup that can't be made (here, the folder is a file) leaves the database as it was, and says why.
            var stuck = await RollBackOneAsync(database);
            var blocked = Path.Combine(folder, "not-a-folder");
            await File.WriteAllTextAsync(blocked, "");
            var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Backup:Folder"] = blocked }).Build();
            var services = factory.Services;
            var schedule = new BackupSchedule(services.GetRequiredService<IServiceScopeFactory>(), settings, services.GetRequiredService<EbookStore>(), services.GetRequiredService<AudioStore>(),
                services.GetRequiredService<CoverStore>(), services.GetRequiredService<OffsiteBackup>(), NullLogger<BackupSchedule>.Instance);
            await using (var old = Open(database))
            {
                var problem = await schedule.BeforeMigratingAsync(old);
                Assert.Contains("Backup__BeforeMigrating=false", problem);
                Assert.Equal(stuck, (await old.Database.GetAppliedMigrationsAsync()).Last());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string[] Backups(string folder) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder, BackupSchedule.Prefix + "*.zip") : [];

    // Takes the database back to the migration before the newest, as a shelf of the version before would have it.
    private static async Task<string> RollBackOneAsync(string path)
    {
        await using var db = Open(path);
        var before = db.Database.GetMigrations().SkipLast(1).Last();
        await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(before);
        return before;
    }

    private static ShelfDb Open(string path) =>
        new(new DbContextOptionsBuilder<ShelfDb>().UseSqlite($"Data Source={path};Pooling=False").Options, new ShelfReader(new HttpContextAccessor()));
}

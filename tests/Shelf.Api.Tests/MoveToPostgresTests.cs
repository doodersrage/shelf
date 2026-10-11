using Microsoft.EntityFrameworkCore;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class MoveToPostgresTests
{
    [Fact]
    public async Task A_sqlite_shelf_moves_into_postgres_with_its_ids_and_new_rows_follow_them()
    {
        if (Environment.GetEnvironmentVariable("SHELF_TEST_POSTGRES") is not { Length: > 0 } postgres)
        {
            // Needs a PostgreSQL server to move into.
            return;
        }

        var file = Path.Combine(Path.GetTempPath(), $"shelf-move-{Guid.NewGuid():N}.db");
        var target = $"{postgres};Database=shelf_move_{Guid.NewGuid():N}";
        try
        {
            await using (var sqlite = new ShelfDb(new DbContextOptionsBuilder<ShelfDb>().UseSqlite($"Data Source={file}").Options))
            {
                await sqlite.Database.MigrateAsync();
                var reader = new Reader { Name = "Mover", NormalizedName = "MOVER", CreatedAt = DateTimeOffset.UtcNow };
                var child = new Reader { Name = "Little", NormalizedName = "LITTLE", CreatedAt = DateTimeOffset.UtcNow, Restricted = true };
                sqlite.Readers.AddRange(reader, child);
                await sqlite.SaveChangesAsync();
                child.GuardianId = reader.Id;
                var tag = new Tag { Name = "sea" };
                for (var i = 0; i < 5; i++)
                {
                    sqlite.Books.Add(new Book { OwnerId = reader.Id, Title = $"Book {i}", Author = "Someone", AddedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.FromHours(-5)), Tags = { tag } });
                }

                await sqlite.SaveChangesAsync();
                sqlite.Quotes.Add(new Quote { BookId = (await sqlite.Books.IgnoreQueryFilters().FirstAsync()).Id, Text = "Call me Ishmael.", NotedAt = DateTimeOffset.UtcNow });
                await sqlite.SaveChangesAsync();
            }

            var moved = await MoveToPostgres.RunAsync($"Data Source={file}", target);
            Assert.Null(moved.Problem);
            Assert.True(moved.Rows >= 14, $"Moved {moved.Rows} rows.");

            await using var pg = new PostgresShelfDb(new DbContextOptionsBuilder<PostgresShelfDb>().UseNpgsql(target).Options);
            Assert.Equal(5, await pg.Books.IgnoreQueryFilters().CountAsync());
            Assert.Equal(5, await pg.Books.IgnoreQueryFilters().CountAsync(book => book.Tags.Any(tag => tag.Name == "sea")));
            Assert.Equal(1, await pg.Quotes.IgnoreQueryFilters().CountAsync());
            var guardian = await pg.Readers.Where(item => item.Name == "Little").Select(item => item.GuardianId).SingleAsync();
            Assert.Equal(await pg.Readers.Where(item => item.Name == "Mover").Select(item => (int?)item.Id).SingleAsync(), guardian);

            // A new book takes the next id, after those moved.
            var highest = await pg.Books.IgnoreQueryFilters().MaxAsync(book => book.Id);
            var owner = await pg.Readers.Where(item => item.Name == "Mover").Select(item => item.Id).SingleAsync();
            var added = new Book { OwnerId = owner, Title = "New", Author = "Someone", AddedAt = DateTimeOffset.UtcNow };
            pg.Books.Add(added);
            await pg.SaveChangesAsync();
            Assert.True(added.Id > highest);

            // Moving again into a shelf that has one is refused.
            Assert.NotNull((await MoveToPostgres.RunAsync($"Data Source={file}", target)).Problem);
            await pg.Database.EnsureDeletedAsync();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }
}

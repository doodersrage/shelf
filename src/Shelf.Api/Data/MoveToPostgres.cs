using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Shelf.Api.Data;

public sealed record MoveResult(int Tables, int Rows, string? Problem);

// Moves a shelf from its SQLite file into an empty PostgreSQL database, row for row, ids and all: run once, with
// --move-to-postgres, when changing over. The files on disk stay where they are, as both read them the same way.
public static class MoveToPostgres
{
    public static async Task<MoveResult> RunAsync(string sqliteConnection, string postgresConnection, CancellationToken cancellationToken = default)
    {
        await using var source = new ShelfDb(new DbContextOptionsBuilder<ShelfDb>().UseSqlite(sqliteConnection).Options);
        await using var target = new PostgresShelfDb(new DbContextOptionsBuilder<PostgresShelfDb>().UseNpgsql(postgresConnection).Options);
        await source.Database.MigrateAsync(cancellationToken);
        await target.Database.MigrateAsync(cancellationToken);
        if (await target.Readers.IgnoreQueryFilters().AnyAsync(cancellationToken) || await target.Books.IgnoreQueryFilters().AnyAsync(cancellationToken))
        {
            return new MoveResult(0, 0, "The PostgreSQL database has a shelf in it already; move into an empty one.");
        }

        var types = source.Model.GetEntityTypes().Where(type => !type.IsOwned() && type.FindPrimaryKey() is not null && type.GetTableName() is not null).ToList();
        target.ChangeTracker.AutoDetectChangesEnabled = false;
        var rows = 0;
        foreach (var type in types)
        {
            foreach (var item in await LoadAsync(source, type, cancellationToken))
            {
                if (type.HasSharedClrType)
                {
                    target.Set<Dictionary<string, object>>(type.Name).Add((Dictionary<string, object>)item);
                }
                else
                {
                    target.Add(item);
                }

                rows++;
            }
        }

        // One save, so the rows go in the order their keys need, whatever order they were read in.
        target.ChangeTracker.DetectChanges();
        await using (var transaction = await target.Database.BeginTransactionAsync(cancellationToken))
        {
            await target.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // Ids given by hand leave PostgreSQL's counters behind; they start after the highest now.
        foreach (var type in types)
        {
            var key = type.FindPrimaryKey()!.Properties;
            if (key.Count == 1 && key[0].ClrType == typeof(int) && key[0].ValueGenerated == ValueGenerated.OnAdd && type.GetTableName() is { } table)
            {
                var column = key[0].GetColumnName(StoreObjectIdentifier.Table(table, type.GetSchema()))!;
#pragma warning disable EF1002
                await target.Database.ExecuteSqlRawAsync(
                    $"SELECT setval(pg_get_serial_sequence('\"{table}\"', '{column}'), GREATEST((SELECT COALESCE(MAX(\"{column}\"), 0) FROM \"{table}\"), 1))", cancellationToken);
#pragma warning restore EF1002
            }
        }

        return new MoveResult(types.Count, rows, null);
    }

    // Every row of one table, past the filters that show each reader only their own.
    private static async Task<List<object>> LoadAsync(ShelfDb db, IEntityType type, CancellationToken cancellationToken)
    {
        if (type.HasSharedClrType)
        {
            return [.. await db.Set<Dictionary<string, object>>(type.Name).IgnoreQueryFilters().AsNoTracking().ToListAsync(cancellationToken)];
        }

        var set = typeof(MoveToPostgres).GetMethod(nameof(Rows), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.MakeGenericMethod(type.ClrType);
        return await (Task<List<object>>)set.Invoke(null, [db, cancellationToken])!;
    }

    private static async Task<List<object>> Rows<T>(ShelfDb db, CancellationToken cancellationToken)
        where T : class =>
        [.. await db.Set<T>().IgnoreQueryFilters().AsNoTracking().ToListAsync(cancellationToken)];
}

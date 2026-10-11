using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Shelf.Api.Books;
using Shelf.Api.Readers;

namespace Shelf.Api.Data;

// The same shelf in PostgreSQL (Database:Provider = Postgres): its own migrations, in Data/PostgresMigrations, as the
// two databases write tables differently. Times are kept in UTC, as PostgreSQL's timestamptz asks.
public sealed class PostgresShelfDb(DbContextOptions<PostgresShelfDb> options, ShelfReader? reader = null, ShelfEvents? events = null)
    : ShelfDb(options, reader, events)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<UtcConverter>();
    }

    private sealed class UtcConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(value => value.ToUniversalTime(), value => value);
}

// For dotnet ef, making the PostgreSQL migrations: any server will do, as none is reached.
public sealed class PostgresShelfDbDesign : IDesignTimeDbContextFactory<PostgresShelfDb>
{
    public PostgresShelfDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PostgresShelfDb>().UseNpgsql("Host=localhost;Database=shelf;Username=shelf").Options);
}

// SQLite's own, so the migration tools asked for ShelfDb do not take the PostgreSQL one above for it.
public sealed class ShelfDbDesign : IDesignTimeDbContextFactory<ShelfDb>
{
    public ShelfDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ShelfDb>().UseSqlite("Data Source=shelf.db").Options);
}

// Hands out PostgresShelfDb wherever a ShelfDb is asked for.
public sealed class PostgresShelfDbFactory(IDbContextFactory<PostgresShelfDb> inner) : IDbContextFactory<ShelfDb>
{
    public ShelfDb CreateDbContext() => inner.CreateDbContext();
}

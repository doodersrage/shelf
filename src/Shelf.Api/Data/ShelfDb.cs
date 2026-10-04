using Microsoft.EntityFrameworkCore;
using Shelf.Api.Books;

namespace Shelf.Api.Data;

public sealed class ShelfDb(DbContextOptions<ShelfDb> options) : DbContext(options)
{
    public DbSet<Book> Books => Set<Book>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Book>(book =>
        {
            book.Property(b => b.Title).HasMaxLength(200).IsRequired();
            book.Property(b => b.Author).HasMaxLength(200).IsRequired();
            book.Property(b => b.Status).HasConversion<string>().HasMaxLength(16);
        });
    }
}

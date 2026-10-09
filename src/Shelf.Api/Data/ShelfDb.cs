using Microsoft.EntityFrameworkCore;
using Shelf.Api.Books;

namespace Shelf.Api.Data;

public sealed class ShelfDb(DbContextOptions<ShelfDb> options) : DbContext(options)
{
    public DbSet<Book> Books => Set<Book>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<Quote> Quotes => Set<Quote>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Book>(book =>
        {
            book.Property(b => b.Title).HasMaxLength(200).IsRequired();
            book.Property(b => b.Author).HasMaxLength(200).IsRequired();
            book.Property(b => b.Status).HasConversion<string>().HasMaxLength(16);
            book.Property(b => b.Isbn).HasMaxLength(13);
            book.Property(b => b.Notes).HasMaxLength(4000);
            book.Property(b => b.LoanedTo).HasMaxLength(120);
            book.HasIndex(b => b.Status);
            book.HasMany(b => b.Tags)
                .WithMany(tag => tag.Books)
                .UsingEntity(join => join.ToTable("BookTags"));
            book.HasMany(b => b.Quotes)
                .WithOne(quote => quote.Book)
                .HasForeignKey(quote => quote.BookId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Tag>(tag =>
        {
            tag.Property(t => t.Name).HasMaxLength(BookRules.MaxTagLength).IsRequired();
            tag.HasIndex(t => t.Name).IsUnique();
        });

        modelBuilder.Entity<Quote>(quote =>
        {
            quote.Property(q => q.Text).HasMaxLength(1000).IsRequired();
        });
    }
}

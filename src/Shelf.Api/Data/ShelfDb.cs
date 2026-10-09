using Microsoft.EntityFrameworkCore;
using Shelf.Api.Books;

namespace Shelf.Api.Data;

public sealed class ShelfDb(DbContextOptions<ShelfDb> options) : DbContext(options)
{
    public DbSet<Book> Books => Set<Book>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<ReadingSession> Sessions => Set<ReadingSession>();
    public DbSet<ShelfSetting> Settings => Set<ShelfSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Book>(book =>
        {
            book.Property(b => b.Title).HasMaxLength(200).IsRequired();
            book.Property(b => b.Author).HasMaxLength(200).IsRequired();
            book.Property(b => b.Status).HasConversion<string>().HasMaxLength(16);
            book.Property(b => b.Isbn).HasMaxLength(13);
            book.Property(b => b.Notes).HasMaxLength(4000);
            book.Property(b => b.Review).HasMaxLength(4000);
            book.Property(b => b.LoanedTo).HasMaxLength(120);
            book.Property(b => b.Subtitle).HasMaxLength(200);
            book.Property(b => b.Publisher).HasMaxLength(200);
            book.Property(b => b.Language).HasMaxLength(40);
            book.Property(b => b.Series).HasMaxLength(200);
            book.Property(b => b.CoverUrl).HasMaxLength(500);
            book.Property(b => b.Location).HasMaxLength(80);
            book.Property(b => b.Translator).HasMaxLength(200);
            book.Property(b => b.OriginalTitle).HasMaxLength(200);
            book.Property(b => b.Inscription).HasMaxLength(500);
            book.Property(b => b.RecommendedBy).HasMaxLength(120);
            book.Property(b => b.Format).HasConversion<string>().HasMaxLength(16);
            book.Property(b => b.Acquisition).HasConversion<string>().HasMaxLength(16);
            book.HasIndex(b => b.Status);
            book.HasIndex(b => b.Author);
            book.HasMany(b => b.Tags)
                .WithMany(tag => tag.Books)
                .UsingEntity(join => join.ToTable("BookTags"));
            book.HasMany(b => b.Quotes)
                .WithOne(quote => quote.Book)
                .HasForeignKey(quote => quote.BookId)
                .OnDelete(DeleteBehavior.Cascade);
            book.HasMany(b => b.Sessions)
                .WithOne(session => session.Book)
                .HasForeignKey(session => session.BookId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReadingSession>(session =>
        {
            session.Property(s => s.Note).HasMaxLength(500);
        });

        modelBuilder.Entity<ShelfSetting>(setting =>
        {
            setting.Property(s => s.Id).ValueGeneratedNever();
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

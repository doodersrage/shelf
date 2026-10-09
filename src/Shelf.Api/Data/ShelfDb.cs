using Microsoft.EntityFrameworkCore;
using Shelf.Api.Books;
using Shelf.Api.Readers;

namespace Shelf.Api.Data;

public sealed class ShelfDb : DbContext
{
    private readonly ShelfReader? _reader;

    public ShelfDb(DbContextOptions<ShelfDb> options, ShelfReader? reader = null)
        : base(options)
    {
        _reader = reader;
        SavingChanges += (_, _) => StampOwners();
    }

    // Every query sees only this reader's books. No reader means no books, never the unclaimed ones.
    public int ReaderId => _reader?.Id ?? 0;

    public DbSet<Reader> Readers => Set<Reader>();
    public DbSet<Book> Books => Set<Book>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<Highlight> Highlights => Set<Highlight>();
    public DbSet<ReadingSession> Sessions => Set<ReadingSession>();
    public DbSet<LoanPlace> LoanPlaces => Set<LoanPlace>();
    public DbSet<LoanAskRow> LoanAsks => Set<LoanAskRow>();
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
            book.Property(b => b.Condition).HasConversion<string>().HasMaxLength(16);
            book.Property(b => b.EbookFileName).HasMaxLength(200);
            book.Property(b => b.EbookStoredName).HasMaxLength(48);
            book.Property(b => b.AudioFileName).HasMaxLength(200);
            book.Property(b => b.AudioStoredName).HasMaxLength(48);
            book.HasOne(b => b.Owner)
                .WithMany()
                .HasForeignKey(b => b.OwnerId)
                .OnDelete(DeleteBehavior.Cascade);
            book.HasOne(b => b.Borrower)
                .WithMany()
                .HasForeignKey(b => b.BorrowerId)
                .OnDelete(DeleteBehavior.SetNull);
            book.HasQueryFilter(b => b.OwnerId == ReaderId);
            book.HasIndex(b => b.Status);
            book.HasIndex(b => b.Author);
            book.HasMany(b => b.Tags)
                .WithMany(tag => tag.Books)
                .UsingEntity(join => join.ToTable("BookTags"));
            book.HasMany(b => b.Quotes)
                .WithOne(quote => quote.Book)
                .HasForeignKey(quote => quote.BookId)
                .OnDelete(DeleteBehavior.Cascade);
            book.HasMany(b => b.Highlights)
                .WithOne(highlight => highlight.Book)
                .HasForeignKey(highlight => highlight.BookId)
                .OnDelete(DeleteBehavior.Cascade);
            book.HasMany(b => b.Sessions)
                .WithOne(session => session.Book)
                .HasForeignKey(session => session.BookId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReadingSession>(session =>
        {
            session.Property(s => s.Note).HasMaxLength(500);
            session.HasQueryFilter(s => s.Book!.OwnerId == ReaderId);
        });

        // A setting row belongs to the reader with the same id.
        modelBuilder.Entity<ShelfSetting>(setting =>
        {
            setting.Property(s => s.Id).ValueGeneratedNever();
            setting.Property(s => s.SyncAddress).HasMaxLength(300);
            setting.Property(s => s.SyncKey).HasMaxLength(100);
        });

        modelBuilder.Entity<Reader>(reader =>
        {
            reader.Property(r => r.Name).HasMaxLength(ReaderRules.MaxNameLength).IsRequired();
            reader.Property(r => r.NormalizedName).HasMaxLength(ReaderRules.MaxNameLength).IsRequired();
            reader.Property(r => r.PasswordHash).HasMaxLength(200).IsRequired();
            reader.Property(r => r.KeyHash).HasMaxLength(64);
            reader.HasIndex(r => r.NormalizedName).IsUnique();
            reader.Property(r => r.Stamp).HasMaxLength(64).IsRequired();
            reader.HasIndex(r => r.KeyHash).IsUnique();
        });

        modelBuilder.Entity<LoanAskRow>(ask =>
        {
            ask.ToTable("LoanAsks");
            ask.HasOne<Book>()
                .WithMany()
                .HasForeignKey(item => item.BookId)
                .OnDelete(DeleteBehavior.Cascade);
            ask.HasOne<Reader>()
                .WithMany()
                .HasForeignKey(item => item.ReaderId)
                .OnDelete(DeleteBehavior.Cascade);
            ask.HasIndex(item => new { item.BookId, item.ReaderId }).IsUnique();
        });

        modelBuilder.Entity<Tag>(tag =>
        {
            tag.Property(t => t.Name).HasMaxLength(BookRules.MaxTagLength).IsRequired();
            tag.HasIndex(t => t.Name).IsUnique();
        });

        modelBuilder.Entity<Quote>(quote =>
        {
            quote.Property(q => q.Text).HasMaxLength(1000).IsRequired();
            quote.HasQueryFilter(q => q.Book!.OwnerId == ReaderId);
        });

        modelBuilder.Entity<Highlight>(highlight =>
        {
            highlight.Property(item => item.Text).HasMaxLength(1000).IsRequired();
            highlight.Property(item => item.Note).HasMaxLength(2000);
            highlight.Property(item => item.Prefix).HasMaxLength(80);
            highlight.Property(item => item.Suffix).HasMaxLength(80);
            highlight.HasOne<Reader>()
                .WithMany()
                .HasForeignKey(item => item.ReaderId)
                .OnDelete(DeleteBehavior.Cascade);
            highlight.HasQueryFilter(item => item.Book!.OwnerId == ReaderId && item.ReaderId == null);
        });

        // A borrower's place belongs to the book and the borrower; the owner's query filters never see it.
        modelBuilder.Entity<LoanPlace>(place =>
        {
            place.HasOne<Book>()
                .WithMany()
                .HasForeignKey(item => item.BookId)
                .OnDelete(DeleteBehavior.Cascade);
            place.HasOne<Reader>()
                .WithMany()
                .HasForeignKey(item => item.ReaderId)
                .OnDelete(DeleteBehavior.Cascade);
            place.HasIndex(item => new { item.BookId, item.ReaderId }).IsUnique();
        });
    }

    private void StampOwners()
    {
        if (ReaderId == 0)
        {
            return;
        }

        foreach (var entry in ChangeTracker.Entries<Book>())
        {
            if (entry.State == EntityState.Added && entry.Entity.OwnerId is null)
            {
                entry.Entity.OwnerId = ReaderId;
            }
        }
    }
}

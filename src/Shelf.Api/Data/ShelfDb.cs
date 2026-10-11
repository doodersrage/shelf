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
    public DbSet<OcrScan> OcrScans => Set<OcrScan>();
    public DbSet<OcrPage> OcrPages => Set<OcrPage>();
    public DbSet<BookText> BookTexts => Set<BookText>();
    public DbSet<PasswordReset> PasswordResets => Set<PasswordReset>();
    public DbSet<AudioBookmark> AudioBookmarks => Set<AudioBookmark>();
    public DbSet<ReaderSession> ReaderSessions => Set<ReaderSession>();
    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();
    public DbSet<KosyncPlace> KosyncPlaces => Set<KosyncPlace>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<ReaderPasskey> ReaderPasskeys => Set<ReaderPasskey>();
    public DbSet<ShelfSetting> Settings => Set<ShelfSetting>();
    public DbSet<SavedSearch> SavedSearches => Set<SavedSearch>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<SeriesAlert> SeriesAlerts => Set<SeriesAlert>();
    public DbSet<ListeningDay> ListeningDays => Set<ListeningDay>();
    public DbSet<ReadingDay> ReadingDays => Set<ReadingDay>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<CollectionBook> CollectionBooks => Set<CollectionBook>();
    public DbSet<Club> Clubs => Set<Club>();
    public DbSet<ClubMember> ClubMembers => Set<ClubMember>();
    public DbSet<ClubBook> ClubBooks => Set<ClubBook>();
    public DbSet<ClubPost> ClubPosts => Set<ClubPost>();

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
            book.Property(b => b.Narrator).HasMaxLength(200);
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
        modelBuilder.Entity<SavedSearch>(saved =>
        {
            saved.Property(s => s.Name).HasMaxLength(SavedSearch.MaxNameLength).IsRequired();
            saved.Property(s => s.Query).HasMaxLength(SavedSearch.MaxQueryLength).IsRequired();
            saved.HasIndex(s => s.OwnerId);
            saved.Property(s => s.ShareToken).HasMaxLength(40);
            saved.HasIndex(s => s.ShareToken).IsUnique();
            saved.HasOne<Reader>().WithMany().HasForeignKey(s => s.OwnerId).OnDelete(DeleteBehavior.Cascade);
            saved.HasQueryFilter(s => s.OwnerId == ReaderId);
        });

        modelBuilder.Entity<ReadingDay>(day =>
        {
            day.HasIndex(item => new { item.ReaderId, item.BookId, item.Day }).IsUnique();
            day.HasOne(item => item.Book).WithMany().HasForeignKey(item => item.BookId).OnDelete(DeleteBehavior.Cascade);
            day.HasOne<Reader>().WithMany().HasForeignKey(item => item.ReaderId).OnDelete(DeleteBehavior.Cascade);
            day.HasQueryFilter(item => item.ReaderId == ReaderId);
        });

        // Clubs belong to no one shelf: who may see one is decided by membership, in Clubs.
        modelBuilder.Entity<Club>(club =>
        {
            club.Property(item => item.Name).HasMaxLength(Club.MaxNameLength).IsRequired();
            club.Property(item => item.Description).HasMaxLength(Club.MaxDescriptionLength);
            club.HasOne<Reader>().WithMany().HasForeignKey(item => item.OwnerId).OnDelete(DeleteBehavior.Cascade);
            club.HasMany(item => item.Members).WithOne(member => member.Club).HasForeignKey(member => member.ClubId).OnDelete(DeleteBehavior.Cascade);
            club.HasMany(item => item.Books).WithOne(book => book.Club).HasForeignKey(book => book.ClubId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ClubMember>(member =>
        {
            member.HasKey(item => new { item.ClubId, item.ReaderId });
            member.HasIndex(item => item.ReaderId);
            member.HasOne<Reader>().WithMany().HasForeignKey(item => item.ReaderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ClubBook>(book =>
        {
            book.Property(item => item.Title).HasMaxLength(ClubBook.MaxTitleLength).IsRequired();
            book.Property(item => item.Author).HasMaxLength(ClubBook.MaxTitleLength).IsRequired();
            book.Property(item => item.CoverUrl).HasMaxLength(500);
            book.Property(item => item.Isbn).HasMaxLength(13);
            book.HasMany(item => item.Posts).WithOne(post => post.Book).HasForeignKey(post => post.ClubBookId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ClubPost>(post =>
        {
            post.Property(item => item.Text).HasMaxLength(ClubPost.MaxLength).IsRequired();
            post.HasOne<Reader>().WithMany().HasForeignKey(item => item.ReaderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Collection>(collection =>
        {
            collection.Property(item => item.Name).HasMaxLength(Collection.MaxNameLength).IsRequired();
            collection.Property(item => item.Description).HasMaxLength(Collection.MaxDescriptionLength);
            collection.Property(item => item.ShareToken).HasMaxLength(40);
            collection.HasIndex(item => item.ShareToken).IsUnique();
            collection.HasIndex(item => item.OwnerId);
            collection.HasOne<Reader>().WithMany().HasForeignKey(item => item.OwnerId).OnDelete(DeleteBehavior.Cascade);
            collection.HasMany(item => item.Books).WithOne(entry => entry.Collection).HasForeignKey(entry => entry.CollectionId).OnDelete(DeleteBehavior.Cascade);
            collection.HasQueryFilter(item => item.OwnerId == ReaderId);
        });

        modelBuilder.Entity<CollectionBook>(entry =>
        {
            entry.HasKey(item => new { item.CollectionId, item.BookId });
            entry.HasOne(item => item.Book).WithMany().HasForeignKey(item => item.BookId).OnDelete(DeleteBehavior.Cascade);
            entry.HasIndex(item => item.BookId);
            entry.HasQueryFilter(item => item.Collection!.OwnerId == ReaderId);
        });

        // A listener's own time, on their own books and on books lent to them alike.
        modelBuilder.Entity<ListeningDay>(day =>
        {
            day.HasIndex(item => new { item.ReaderId, item.BookId, item.Day }).IsUnique();
            day.HasOne(item => item.Book).WithMany().HasForeignKey(item => item.BookId).OnDelete(DeleteBehavior.Cascade);
            day.HasOne<Reader>().WithMany().HasForeignKey(item => item.ReaderId).OnDelete(DeleteBehavior.Cascade);
            day.HasQueryFilter(item => item.ReaderId == ReaderId);
        });

        modelBuilder.Entity<SeriesAlert>(alert =>
        {
            alert.Property(a => a.Series).HasMaxLength(200).IsRequired();
            alert.Property(a => a.Author).HasMaxLength(200).IsRequired();
            alert.Property(a => a.Title).HasMaxLength(200).IsRequired();
            alert.Property(a => a.WorkKey).HasMaxLength(60).IsRequired();
            alert.HasIndex(a => new { a.OwnerId, a.WorkKey }).IsUnique();
            alert.HasOne<Reader>().WithMany().HasForeignKey(a => a.OwnerId).OnDelete(DeleteBehavior.Cascade);
            alert.HasQueryFilter(a => a.OwnerId == ReaderId);
        });

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
            reader.Property(r => r.Email).HasMaxLength(EmailRules.MaxAddressLength);
            reader.Property(r => r.TwoFactorSecret).HasMaxLength(500);
            reader.Property(r => r.Culture).HasMaxLength(16);
            reader.HasIndex(r => r.KeyHash).IsUnique();
            reader.Property(r => r.OidcSubject).HasMaxLength(600);
            reader.Property(r => r.KindleEmail).HasMaxLength(EmailRules.MaxAddressLength);
            reader.HasIndex(r => r.OidcSubject).IsUnique();
        });

        // Words read from scanned PDF pages. They go with the book, and a new file gets a new reading.
        modelBuilder.Entity<OcrScan>(scan =>
        {
            scan.Property(item => item.StoredName).HasMaxLength(48).IsRequired();
            scan.HasOne<Book>()
                .WithMany()
                .HasForeignKey(item => item.BookId)
                .OnDelete(DeleteBehavior.Cascade);
            scan.HasIndex(item => new { item.BookId, item.StoredName }).IsUnique();
        });

        modelBuilder.Entity<OcrPage>(page =>
        {
            page.HasOne<OcrScan>()
                .WithMany()
                .HasForeignKey(item => item.ScanId)
                .OnDelete(DeleteBehavior.Cascade);
            page.HasIndex(item => new { item.ScanId, item.Page }).IsUnique();
        });

        modelBuilder.Entity<ReaderSession>(session =>
        {
            session.HasKey(item => item.Id);
            session.Property(item => item.Id).HasMaxLength(32);
            session.Property(item => item.Device).HasMaxLength(200);
            session.HasOne<Reader>().WithMany().HasForeignKey(item => item.ReaderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReaderPasskey>(key =>
        {
            key.HasIndex(item => item.CredentialId).IsUnique();
            key.Property(item => item.CredentialId).HasMaxLength(1024);
            key.Property(item => item.Name).HasMaxLength(80);
            key.HasOne<Reader>().WithMany().HasForeignKey(item => item.ReaderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApiToken>(token =>
        {
            token.Property(item => item.Name).HasMaxLength(ApiToken.MaxNameLength).IsRequired();
            token.Property(item => item.Hash).HasMaxLength(64).IsRequired();
            token.HasIndex(item => item.Hash).IsUnique();
            token.HasIndex(item => item.ReaderId);
            token.HasOne<Reader>().WithMany().HasForeignKey(item => item.ReaderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditEntry>(entry =>
        {
            entry.Property(item => item.ActorName).HasMaxLength(120);
            entry.Property(item => item.TargetName).HasMaxLength(120);
            entry.Property(item => item.Action).HasMaxLength(200);
            entry.Property(item => item.Detail).HasMaxLength(300);
            entry.HasIndex(item => item.ActorId);
            entry.HasIndex(item => item.TargetId);
        });

        modelBuilder.Entity<KosyncPlace>(place =>
        {
            place.HasIndex(item => new { item.ReaderId, item.BookId }).IsUnique();
            place.Property(item => item.Progress).HasMaxLength(500);
            place.Property(item => item.Device).HasMaxLength(100);
            place.Property(item => item.DeviceId).HasMaxLength(100);
            place.HasOne<Reader>().WithMany().HasForeignKey(item => item.ReaderId).OnDelete(DeleteBehavior.Cascade);
            place.HasOne<Book>().WithMany().HasForeignKey(item => item.BookId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RecoveryCode>(code =>
        {
            code.Property(item => item.CodeHash).HasMaxLength(64).IsRequired();
            code.HasOne<Reader>().WithMany().HasForeignKey(item => item.ReaderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AudioBookmark>(bookmark =>
        {
            bookmark.Property(item => item.Note).HasMaxLength(500);
            bookmark.HasOne(item => item.Book)
                .WithMany()
                .HasForeignKey(item => item.BookId)
                .OnDelete(DeleteBehavior.Cascade);
            bookmark.HasOne<Reader>()
                .WithMany()
                .HasForeignKey(item => item.ReaderId)
                .OnDelete(DeleteBehavior.Cascade);
            bookmark.HasQueryFilter(item => item.Book!.OwnerId == ReaderId && item.ReaderId == null);
        });

        modelBuilder.Entity<PasswordReset>(reset =>
        {
            reset.Property(item => item.TokenHash).HasMaxLength(64).IsRequired();
            reset.HasIndex(item => item.TokenHash).IsUnique();
            reset.HasOne<Reader>()
                .WithMany()
                .HasForeignKey(item => item.ReaderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BookText>(text =>
        {
            text.HasOne<OcrScan>()
                .WithMany()
                .HasForeignKey(item => item.ScanId)
                .OnDelete(DeleteBehavior.Cascade);
            text.HasIndex(item => new { item.BookId, item.Part });
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

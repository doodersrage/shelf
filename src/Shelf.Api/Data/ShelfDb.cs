using Microsoft.EntityFrameworkCore;
using Shelf.Api.Books;
using Shelf.Api.Readers;

namespace Shelf.Api.Data;

public class ShelfDb : DbContext
{
    private readonly ShelfReader? _reader;
    private readonly ShelfEvents? _events;
    private List<Func<ShelfEvent?>> _happening = [];

    public ShelfDb(DbContextOptions<ShelfDb> options, ShelfReader? reader = null, ShelfEvents? events = null)
        : this((DbContextOptions)options, reader, events)
    {
    }

    // For PostgresShelfDb, which keeps its own migrations.
    protected ShelfDb(DbContextOptions options, ShelfReader? reader, ShelfEvents? events)
        : base(options)
    {
        _reader = reader;
        _events = events;
        SavingChanges += (_, _) =>
        {
            StampOwners();
            NoteEvents();
        };
        SavedChanges += (_, _) => TellEvents();
        SaveChangesFailed += (_, _) => _happening = [];
    }

    // Every query sees only this reader's books. No reader means no books, never the unclaimed ones.
    public int ReaderId => _reader?.Id ?? 0;

    // PostgreSQL rather than the usual SQLite: the few places that speak to the database directly ask.
    public bool IsPostgres => Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;

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
    public DbSet<KoboEntry> KoboEntries => Set<KoboEntry>();
    public DbSet<Club> Clubs => Set<Club>();
    public DbSet<ClubMember> ClubMembers => Set<ClubMember>();
    public DbSet<ClubBook> ClubBooks => Set<ClubBook>();
    public DbSet<ClubPost> ClubPosts => Set<ClubPost>();
    public DbSet<PushSubscriptionRow> PushSubscriptions => Set<PushSubscriptionRow>();
    public DbSet<Webhook> Webhooks => Set<Webhook>();
    public DbSet<ReadAlongRow> ReadAlongs => Set<ReadAlongRow>();
    public DbSet<SpokenText> SpokenTexts => Set<SpokenText>();
    public DbSet<PartSummary> PartSummaries => Set<PartSummary>();
    public DbSet<Podcast> Podcasts => Set<Podcast>();
    public DbSet<ShelfLink> ShelfLinks => Set<ShelfLink>();
    public DbSet<RemoteReader> RemoteReaders => Set<RemoteReader>();
    public DbSet<RemoteAsk> RemoteAsks => Set<RemoteAsk>();
    public DbSet<LinkedAsk> LinkedAsks => Set<LinkedAsk>();
    public DbSet<PodcastEpisode> PodcastEpisodes => Set<PodcastEpisode>();

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
            book.Property(b => b.Asin).HasMaxLength(10);
            book.Property(b => b.VisitingOwner).HasMaxLength(200);
            book.HasOne(b => b.RemoteBorrower).WithMany().HasForeignKey(b => b.RemoteBorrowerId).OnDelete(DeleteBehavior.SetNull);
            book.HasOne<ShelfLink>().WithMany().HasForeignKey(b => b.VisitingLinkId).OnDelete(DeleteBehavior.SetNull);
            book.Property(b => b.HardcoverState).HasMaxLength(100);
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

        modelBuilder.Entity<KoboEntry>(entry =>
        {
            entry.HasIndex(item => new { item.ReaderId, item.BookId }).IsUnique();
            entry.Property(item => item.Bookmark).HasMaxLength(4000);
            entry.Property(item => item.Status).HasMaxLength(40);
            // Not tied to the book: when the book goes, the entry stays until the next sync tells the Kobo so.
            entry.HasOne<Reader>().WithMany().HasForeignKey(item => item.ReaderId).OnDelete(DeleteBehavior.Cascade);
            entry.HasQueryFilter(item => item.ReaderId == ReaderId);
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

        modelBuilder.Entity<PushSubscriptionRow>(push =>
        {
            push.ToTable("PushSubscriptions");
            push.Property(item => item.Endpoint).HasMaxLength(1000);
            push.Property(item => item.P256dh).HasMaxLength(200);
            push.Property(item => item.Auth).HasMaxLength(100);
            push.Property(item => item.Label).HasMaxLength(120);
            push.HasIndex(item => item.Endpoint).IsUnique();
            push.HasIndex(item => item.ReaderId);
            push.HasOne<Reader>().WithMany().HasForeignKey(item => item.ReaderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ShelfLink>(link =>
        {
            link.Property(item => item.Name).HasMaxLength(120);
            link.Property(item => item.Address).HasMaxLength(500);
            link.Property(item => item.TheirSecret).HasMaxLength(1000);
            link.Property(item => item.OurSecretHash).HasMaxLength(64);
            link.Property(item => item.InviteHash).HasMaxLength(64);
            link.HasIndex(item => item.OurSecretHash);
        });

        modelBuilder.Entity<RemoteReader>(reader =>
        {
            reader.Property(item => item.Name).HasMaxLength(120);
            reader.HasIndex(item => new { item.LinkId, item.RemoteId }).IsUnique();
            reader.HasOne<ShelfLink>().WithMany().HasForeignKey(item => item.LinkId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RemoteAsk>(ask =>
        {
            ask.HasIndex(item => new { item.BookId, item.RemoteReaderId }).IsUnique();
            ask.HasOne<Book>().WithMany().HasForeignKey(item => item.BookId).OnDelete(DeleteBehavior.Cascade);
            ask.HasOne(item => item.Reader).WithMany().HasForeignKey(item => item.RemoteReaderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LinkedAsk>(ask =>
        {
            ask.Property(item => item.Title).HasMaxLength(200);
            ask.Property(item => item.Author).HasMaxLength(200);
            ask.Property(item => item.Owner).HasMaxLength(250);
            ask.HasIndex(item => new { item.ReaderId, item.LinkId, item.RemoteBookId }).IsUnique();
            ask.HasOne<ShelfLink>().WithMany().HasForeignKey(item => item.LinkId).OnDelete(DeleteBehavior.Cascade);
            ask.HasOne<Reader>().WithMany().HasForeignKey(item => item.ReaderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Podcast>(podcast =>
        {
            podcast.Property(item => item.FeedUrl).HasMaxLength(1000);
            podcast.Property(item => item.Title).HasMaxLength(300);
            podcast.Property(item => item.Author).HasMaxLength(200);
            podcast.Property(item => item.Description).HasMaxLength(4000);
            podcast.Property(item => item.ImageUrl).HasMaxLength(1000);
            podcast.Property(item => item.Problem).HasMaxLength(500);
            podcast.HasIndex(item => new { item.OwnerId, item.FeedUrl }).IsUnique();
            podcast.HasOne<Reader>().WithMany().HasForeignKey(item => item.OwnerId).OnDelete(DeleteBehavior.Cascade);
            podcast.HasQueryFilter(item => item.OwnerId == ReaderId);
            podcast.HasMany(item => item.Episodes).WithOne(item => item.Podcast).HasForeignKey(item => item.PodcastId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PodcastEpisode>(episode =>
        {
            episode.Property(item => item.Guid).HasMaxLength(500);
            episode.Property(item => item.Title).HasMaxLength(300);
            episode.Property(item => item.Summary).HasMaxLength(4000);
            episode.Property(item => item.AudioUrl).HasMaxLength(2000);
            episode.Property(item => item.ContentType).HasMaxLength(100);
            episode.Property(item => item.StoredName).HasMaxLength(100);
            episode.HasIndex(item => new { item.PodcastId, item.Guid }).IsUnique();
        });

        modelBuilder.Entity<PartSummary>(summary =>
        {
            summary.Property(item => item.Source).HasMaxLength(48);
            summary.Property(item => item.Language).HasMaxLength(8);
            summary.HasIndex(item => new { item.BookId, item.Source, item.Language, item.Part }).IsUnique();
            summary.HasOne<Book>().WithMany().HasForeignKey(item => item.BookId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SpokenText>(text =>
        {
            text.Property(item => item.AudioStoredName).HasMaxLength(48);
            text.HasIndex(item => item.BookId);
            text.HasOne<Book>().WithMany().HasForeignKey(item => item.BookId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReadAlongRow>(row =>
        {
            row.ToTable("ReadAlongs");
            row.HasKey(item => item.BookId);
            row.Property(item => item.State).HasConversion<string>().HasMaxLength(16);
            row.Property(item => item.Problem).HasMaxLength(500);
            row.Property(item => item.AudioStoredName).HasMaxLength(48);
            row.Property(item => item.EbookStoredName).HasMaxLength(48);
            row.HasOne<Book>().WithOne().HasForeignKey<ReadAlongRow>(item => item.BookId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Webhook>(hook =>
        {
            hook.Property(item => item.Url).HasMaxLength(1000);
            hook.Property(item => item.Format).HasConversion<string>().HasMaxLength(16);
            hook.Property(item => item.Events).HasMaxLength(500);
            hook.Property(item => item.Secret).HasMaxLength(1000);
            hook.HasIndex(item => item.ReaderId);
            hook.HasOne<Reader>().WithMany().HasForeignKey(item => item.ReaderId).OnDelete(DeleteBehavior.Cascade);
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
            reader.HasOne<Reader>().WithMany().HasForeignKey(r => r.GuardianId).OnDelete(DeleteBehavior.SetNull);
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
            reader.Property(r => r.ReadwiseToken).HasMaxLength(1000);
            reader.Property(r => r.HardcoverToken).HasMaxLength(6000);
            reader.Property(r => r.NtfyUrl).HasMaxLength(500);
            reader.Property(r => r.CalendarTokenHash).HasMaxLength(64);
            reader.Property(r => r.PublicToken).HasMaxLength(40);
            reader.HasIndex(r => r.PublicToken).IsUnique();
            reader.Property(r => r.NtfyToken).HasMaxLength(1000);
            reader.Property(r => r.GotifyUrl).HasMaxLength(500);
            reader.Property(r => r.GotifyToken).HasMaxLength(1000);
            reader.Property(r => r.KoboTokenHash).HasMaxLength(64);
            reader.HasIndex(r => r.KoboTokenHash).IsUnique();
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

    // What this save will mean, read before it while the old values are known, and told once it has gone through,
    // when new rows have their ids.
    private void NoteEvents()
    {
        if (_events is null)
        {
            return;
        }

        var actor = ReaderId;
        foreach (var entry in ChangeTracker.Entries<Book>())
        {
            var book = entry.Entity;
            if (entry.State == EntityState.Added)
            {
                _happening.Add(() => new ShelfEvent(EventKinds.BookAdded, actor, book.Id));
                continue;
            }

            if (entry.State != EntityState.Modified)
            {
                continue;
            }

            var status = entry.Property(item => item.Status);
            if (status.IsModified && !Equals(status.OriginalValue, status.CurrentValue) && book.Status is BookStatus.Reading or BookStatus.Finished)
            {
                var kind = book.Status == BookStatus.Finished ? EventKinds.BookFinished : EventKinds.BookStarted;
                _happening.Add(() => new ShelfEvent(kind, actor, book.Id));
            }

            var borrower = entry.Property(item => item.BorrowerId);
            if (borrower.IsModified && borrower.OriginalValue != borrower.CurrentValue)
            {
                if (borrower.CurrentValue is int lentTo)
                {
                    _happening.Add(() => new ShelfEvent(EventKinds.BookLent, actor, book.Id, lentTo));
                }

                if (borrower.OriginalValue is int returnedBy)
                {
                    _happening.Add(() => new ShelfEvent(EventKinds.BookReturned, actor, book.Id, returnedBy));
                }
            }
        }

        foreach (var ask in ChangeTracker.Entries<LoanAskRow>().Where(entry => entry.State == EntityState.Added).Select(entry => entry.Entity))
        {
            _happening.Add(() => new ShelfEvent(EventKinds.LoanAsked, actor, ask.BookId, ask.ReaderId));
        }

        foreach (var post in ChangeTracker.Entries<ClubPost>().Where(entry => entry.State == EntityState.Added).Select(entry => entry.Entity))
        {
            _happening.Add(() => new ShelfEvent(EventKinds.ClubPosted, actor, ReaderId: post.ReaderId, PostId: post.Id));
        }

        foreach (var member in ChangeTracker.Entries<ClubMember>().Where(entry => entry.State == EntityState.Added).Select(entry => entry.Entity))
        {
            _happening.Add(() => new ShelfEvent(EventKinds.ClubJoined, actor, ReaderId: member.ReaderId, ClubId: member.ClubId));
        }

        // New books in series come many at once, so a save tells each reader once, with how many.
        foreach (var found in ChangeTracker.Entries<SeriesAlert>().Where(entry => entry.State == EntityState.Added).GroupBy(entry => entry.Entity.OwnerId))
        {
            var (owner, count) = (found.Key, found.Count());
            _happening.Add(() => new ShelfEvent(EventKinds.SeriesFound, owner, ReaderId: owner, Count: count));
        }
    }

    private void TellEvents()
    {
        var happened = _happening;
        _happening = [];
        foreach (var make in happened)
        {
            if (make() is { } shelfEvent)
            {
                _events!.Publish(shelfEvent);
            }
        }
    }
}

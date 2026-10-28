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
            History.Note(this);
            LoanHistory.Note(this);
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
    public DbSet<SiteSetting> SiteSettings => Set<SiteSetting>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<AudioBookmark> AudioBookmarks => Set<AudioBookmark>();
    public DbSet<ReaderSession> ReaderSessions => Set<ReaderSession>();
    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();
    public DbSet<KosyncPlace> KosyncPlaces => Set<KosyncPlace>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<ReaderPasskey> ReaderPasskeys => Set<ReaderPasskey>();
    public DbSet<ShelfSetting> Settings => Set<ShelfSetting>();
    public DbSet<SavedSearch> SavedSearches => Set<SavedSearch>();
    public DbSet<AbsLink> AbsLinks => Set<AbsLink>();
    public DbSet<AbsSyncSetting> AbsSyncs => Set<AbsSyncSetting>();
    public DbSet<FetchSetting> FetchSettings => Set<FetchSetting>();
    public DbSet<AuthorFollow> AuthorFollows => Set<AuthorFollow>();
    public DbSet<AuthorAlert> AuthorAlerts => Set<AuthorAlert>();
    public DbSet<FetchGrab> FetchGrabs => Set<FetchGrab>();
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
    public DbSet<PastRead> PastReads => Set<PastRead>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<BookLink> BookLinks => Set<BookLink>();
    public DbSet<CopyPhoto> CopyPhotos => Set<CopyPhoto>();
    public DbSet<ClubMeeting> ClubMeetings => Set<ClubMeeting>();
    public DbSet<ClubQuestion> ClubQuestions => Set<ClubQuestion>();
    public DbSet<ReadingGoal> ReadingGoals => Set<ReadingGoal>();
    public DbSet<EventRecord> EventRecords => Set<EventRecord>();
    public DbSet<TrashItem> TrashItems => Set<TrashItem>();
    public DbSet<ChangeBatch> ChangeBatches => Set<ChangeBatch>();
    public DbSet<BookChange> BookChanges => Set<BookChange>();
    public DbSet<LoanRecord> LoanRecords => Set<LoanRecord>();
    public DbSet<ScanSession> ScanSessions => Set<ScanSession>();
    public DbSet<FileRecord> FileRecords => Set<FileRecord>();
    public DbSet<NotDuplicate> NotDuplicates => Set<NotDuplicate>();
    public DbSet<DetailProposal> DetailProposals => Set<DetailProposal>();
    public DbSet<ImportRecipe> ImportRecipes => Set<ImportRecipe>();
    public DbSet<ResearchTopic> ResearchTopics => Set<ResearchTopic>();
    public DbSet<ResearchNote> ResearchNotes => Set<ResearchNote>();
    public DbSet<VocabularyWord> VocabularyWords => Set<VocabularyWord>();
    public DbSet<ParallelPassage> ParallelPassages => Set<ParallelPassage>();
    public DbSet<StoragePlace> StoragePlaces => Set<StoragePlace>();
    public DbSet<PositionReport> PositionReports => Set<PositionReport>();
    public DbSet<PositionChoiceRow> PositionChoices => Set<PositionChoiceRow>();
    public DbSet<AutomationRule> AutomationRules => Set<AutomationRule>();
    public DbSet<AutomationRun> AutomationRuns => Set<AutomationRun>();
    public DbSet<LegacyPlan> LegacyPlans => Set<LegacyPlan>();
    public DbSet<LegacyRecipient> LegacyRecipients => Set<LegacyRecipient>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Book>(book =>
        {
            book.Property(b => b.Title).HasMaxLength(200).IsRequired();
            book.Property(b => b.EbookCfi).HasMaxLength(1000);
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
            book.Property(b => b.PreviousOwners).HasMaxLength(500);
            book.Property(b => b.Format).HasConversion<string>().HasMaxLength(16);
            book.Property(b => b.Acquisition).HasConversion<string>().HasMaxLength(16);
            book.Property(b => b.Condition).HasConversion<string>().HasMaxLength(16);
            book.Property(b => b.PricePaid).HasPrecision(12, 2);
            book.Property(b => b.SoldFor).HasPrecision(12, 2);
            book.Property(b => b.LeftHow).HasConversion<string>().HasMaxLength(16);
            book.Property(b => b.LeftTo).HasMaxLength(120);
            book.Property(b => b.HoldNote).HasMaxLength(80);
            book.Property(b => b.HomeLocation).HasMaxLength(80);
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
            book.HasMany(b => b.PastReads)
                .WithOne(read => read.Book)
                .HasForeignKey(read => read.BookId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PastRead>(read =>
        {
            read.Property(item => item.Thoughts).HasMaxLength(PastRead.MaxThoughtsLength);
            read.HasIndex(item => item.FinishedOn);
            read.HasQueryFilter(item => item.Book!.OwnerId == ReaderId);
        });

        modelBuilder.Entity<JournalEntry>(entry =>
        {
            entry.Property(item => item.Text).HasMaxLength(JournalEntry.MaxTextLength).IsRequired();
            entry.HasIndex(item => new { item.OwnerId, item.Date });
            entry.HasOne<Reader>().WithMany().HasForeignKey(item => item.OwnerId).OnDelete(DeleteBehavior.Cascade);
            entry.HasOne<Book>().WithMany().HasForeignKey(item => item.BookId).OnDelete(DeleteBehavior.SetNull);
            entry.HasQueryFilter(item => item.OwnerId == ReaderId);
        });

        // Both ends are the same reader's books; deleting either takes the link.
        modelBuilder.Entity<BookLink>(link =>
        {
            link.Property(item => item.Kind).HasConversion<string>().HasMaxLength(16);
            link.Property(item => item.Note).HasMaxLength(BookLink.MaxNoteLength);
            link.HasOne(item => item.From).WithMany().HasForeignKey(item => item.FromBookId).OnDelete(DeleteBehavior.Cascade);
            link.HasOne(item => item.To).WithMany().HasForeignKey(item => item.ToBookId).OnDelete(DeleteBehavior.Cascade);
            link.HasIndex(item => item.FromBookId);
            link.HasIndex(item => item.ToBookId);
            link.HasQueryFilter(item => item.From!.OwnerId == ReaderId);
        });

        // Kept by the reader whose it was; the purge reaches every reader's by ignoring the filter.
        modelBuilder.Entity<TrashItem>(item =>
        {
            item.Property(row => row.Kind).HasConversion<string>().HasMaxLength(16);
            item.Property(row => row.Title).HasMaxLength(TrashItem.MaxTitleLength).IsRequired();
            item.Property(row => row.Detail).HasMaxLength(TrashItem.MaxDetailLength);
            item.Property(row => row.Payload).IsRequired();
            item.HasIndex(row => new { row.OwnerId, row.DeletedAt });
            item.HasIndex(row => row.BatchId);
            item.HasOne<Reader>().WithMany().HasForeignKey(row => row.OwnerId).OnDelete(DeleteBehavior.Cascade);
            item.HasOne<ChangeBatch>().WithMany().HasForeignKey(row => row.BatchId).OnDelete(DeleteBehavior.SetNull);
            item.HasQueryFilter(row => row.OwnerId == ReaderId);
        });

        modelBuilder.Entity<ChangeBatch>(batch =>
        {
            batch.Property(item => item.Kind).HasConversion<string>().HasMaxLength(16);
            batch.Property(item => item.Summary).HasMaxLength(ChangeBatch.MaxSummaryLength).IsRequired();
            batch.HasIndex(item => new { item.OwnerId, item.Id });
            batch.HasOne<Reader>().WithMany().HasForeignKey(item => item.OwnerId).OnDelete(DeleteBehavior.Cascade);
        });

        // A change outlives its book while the book is in the trash, pointing nowhere until it comes back.
        modelBuilder.Entity<BookChange>(change =>
        {
            change.Property(item => item.Field).HasMaxLength(40).IsRequired();
            change.HasIndex(item => new { item.BookId, item.Id });
            change.HasIndex(item => item.BatchId);
            change.HasIndex(item => item.At);
            change.HasOne(item => item.Book).WithMany().HasForeignKey(item => item.BookId).OnDelete(DeleteBehavior.SetNull);
            change.HasOne<ChangeBatch>().WithMany().HasForeignKey(item => item.BatchId).OnDelete(DeleteBehavior.SetNull);
            change.HasOne<Reader>().WithMany().HasForeignKey(item => item.OwnerId).OnDelete(DeleteBehavior.Cascade);
            change.HasQueryFilter(item => item.OwnerId == ReaderId);
        });

        modelBuilder.Entity<LoanRecord>(record =>
        {
            record.Property(item => item.Borrower).HasMaxLength(120).IsRequired();
            record.Property(item => item.Note).HasMaxLength(LoanRecord.MaxNoteLength);
            record.Property(item => item.ConditionOut).HasConversion<string>().HasMaxLength(16);
            record.Property(item => item.ConditionBack).HasConversion<string>().HasMaxLength(16);
            record.HasIndex(item => new { item.BookId, item.ReturnedOn });
            record.HasIndex(item => new { item.OwnerId, item.ReturnedOn });
            record.HasOne(item => item.Book).WithMany().HasForeignKey(item => item.BookId).OnDelete(DeleteBehavior.Cascade);
            record.HasOne<Reader>().WithMany().HasForeignKey(item => item.BorrowerId).OnDelete(DeleteBehavior.SetNull);
            record.HasQueryFilter(item => item.OwnerId == ReaderId);
        });

        modelBuilder.Entity<ScanSession>(session =>
        {
            session.Property(item => item.Kind).HasConversion<string>().HasMaxLength(16);
            session.Property(item => item.Location).HasMaxLength(80);
            session.HasIndex(item => item.OwnerId);
            session.HasOne<Reader>().WithMany().HasForeignKey(item => item.OwnerId).OnDelete(DeleteBehavior.Cascade);
            session.HasQueryFilter(item => item.OwnerId == ReaderId);
        });

        // The server's own: what each stored file was when first seen, and how it stood when last looked at.
        modelBuilder.Entity<FileRecord>(record =>
        {
            record.Property(item => item.Kind).HasConversion<string>().HasMaxLength(16);
            record.Property(item => item.StoredName).HasMaxLength(100).IsRequired();
            record.Property(item => item.Sha256).HasMaxLength(64);
            record.Property(item => item.Problem).HasConversion<string>().HasMaxLength(16);
            record.HasIndex(item => new { item.Kind, item.StoredName }).IsUnique();
            record.HasIndex(item => item.Problem);
        });

        modelBuilder.Entity<NotDuplicate>(pair =>
        {
            pair.HasIndex(item => new { item.OwnerId, item.BookId, item.OtherId }).IsUnique();
            pair.HasOne<Book>().WithMany().HasForeignKey(item => item.BookId).OnDelete(DeleteBehavior.Cascade);
            pair.HasOne<Book>().WithMany().HasForeignKey(item => item.OtherId).OnDelete(DeleteBehavior.Cascade);
            pair.HasOne<Reader>().WithMany().HasForeignKey(item => item.OwnerId).OnDelete(DeleteBehavior.Cascade);
            pair.HasQueryFilter(item => item.OwnerId == ReaderId);
        });

        modelBuilder.Entity<StoragePlace>(place =>
        {
            place.Property(item => item.Name).HasMaxLength(StoragePlace.MaxNameLength).IsRequired();
            place.Property(item => item.Kind).HasConversion<string>().HasMaxLength(16);
            place.Property(item => item.Note).HasMaxLength(StoragePlace.MaxNoteLength);
            place.HasIndex(item => item.OwnerId);
            place.HasOne<StoragePlace>().WithMany().HasForeignKey(item => item.ParentId).OnDelete(DeleteBehavior.Restrict);
            place.HasOne<StoragePlace>().WithMany().HasForeignKey(item => item.DestinationId).OnDelete(DeleteBehavior.Restrict);
            place.HasOne<Reader>().WithMany().HasForeignKey(item => item.OwnerId).OnDelete(DeleteBehavior.Cascade);
            place.HasQueryFilter(item => item.OwnerId == ReaderId);
        });

        // Places told by each source, the reader's own whether the book is theirs or lent to them.
        modelBuilder.Entity<PositionReport>(report =>
        {
            report.Property(item => item.Source).HasMaxLength(PositionReport.MaxSourceLength).IsRequired();
            report.Property(item => item.Device).HasMaxLength(PositionReport.MaxDeviceLength).IsRequired();
            report.Property(item => item.EbookCfi).HasMaxLength(1000);
            report.HasIndex(item => new { item.ReaderId, item.BookId, item.Source, item.Device }).IsUnique();
            report.HasOne<Book>().WithMany().HasForeignKey(item => item.BookId).OnDelete(DeleteBehavior.Cascade);
            report.HasOne<Reader>().WithMany().HasForeignKey(item => item.ReaderId).OnDelete(DeleteBehavior.Cascade);
            report.HasQueryFilter(item => item.ReaderId == ReaderId);
        });

        modelBuilder.Entity<PositionChoiceRow>(choice =>
        {
            choice.ToTable("PositionChoices");
            choice.Property(item => item.Source).HasMaxLength(PositionReport.MaxSourceLength).IsRequired();
            choice.Property(item => item.Device).HasMaxLength(PositionReport.MaxDeviceLength).IsRequired();
            choice.Property(item => item.Place).HasMaxLength(200).IsRequired();
            choice.Property(item => item.Before).HasMaxLength(200).IsRequired();
            choice.HasIndex(item => new { item.ReaderId, item.BookId });
            choice.HasOne<Book>().WithMany().HasForeignKey(item => item.BookId).OnDelete(DeleteBehavior.Cascade);
            choice.HasOne<Reader>().WithMany().HasForeignKey(item => item.ReaderId).OnDelete(DeleteBehavior.Cascade);
            choice.HasQueryFilter(item => item.ReaderId == ReaderId);
        });

        modelBuilder.Entity<AutomationRule>(rule =>
        {
            rule.Property(item => item.Name).HasMaxLength(AutomationRule.MaxNameLength).IsRequired();
            rule.Property(item => item.Trigger).HasConversion<string>().HasMaxLength(16);
            rule.Property(item => item.Conditions).HasMaxLength(2000).IsRequired();
            rule.Property(item => item.Actions).HasMaxLength(4000).IsRequired();
            rule.HasIndex(item => new { item.OwnerId, item.Trigger });
            rule.HasOne<Reader>().WithMany().HasForeignKey(item => item.OwnerId).OnDelete(DeleteBehavior.Cascade);
            rule.HasQueryFilter(item => item.OwnerId == ReaderId);
        });

        modelBuilder.Entity<AutomationRun>(run =>
        {
            run.Property(item => item.Outcome).HasMaxLength(AutomationRun.MaxOutcomeLength).IsRequired();
            run.HasIndex(item => new { item.OwnerId, item.RuleId });
            run.HasOne<AutomationRule>().WithMany().HasForeignKey(item => item.RuleId).OnDelete(DeleteBehavior.Cascade);
            run.HasOne<Book>().WithMany().HasForeignKey(item => item.BookId).OnDelete(DeleteBehavior.SetNull);
            run.HasQueryFilter(item => item.OwnerId == ReaderId);
        });

        modelBuilder.Entity<LegacyPlan>(plan =>
        {
            plan.HasIndex(item => item.OwnerId).IsUnique();
            plan.HasOne<Reader>().WithMany().HasForeignKey(item => item.OwnerId).OnDelete(DeleteBehavior.Cascade);
            plan.HasQueryFilter(item => item.OwnerId == ReaderId);
        });

        modelBuilder.Entity<LegacyRecipient>(recipient =>
        {
            recipient.Property(item => item.Name).IsRequired();
            recipient.Property(item => item.Tag).HasMaxLength(BookRules.MaxTagLength);
            recipient.HasIndex(item => item.OwnerId);
            recipient.HasOne<Collection>().WithMany().HasForeignKey(item => item.CollectionId).OnDelete(DeleteBehavior.SetNull);
            recipient.HasOne<Reader>().WithMany().HasForeignKey(item => item.OwnerId).OnDelete(DeleteBehavior.Cascade);
            recipient.HasQueryFilter(item => item.OwnerId == ReaderId);
        });

        modelBuilder.Entity<ImportRecipe>(recipe =>
        {
            recipe.Property(item => item.Name).HasMaxLength(ImportRecipe.MaxNameLength).IsRequired();
            recipe.Property(item => item.Spec).IsRequired();
            recipe.HasIndex(item => new { item.OwnerId, item.Name }).IsUnique();
            recipe.HasOne<Reader>().WithMany().HasForeignKey(item => item.OwnerId).OnDelete(DeleteBehavior.Cascade);
            recipe.HasQueryFilter(item => item.OwnerId == ReaderId);
        });

        modelBuilder.Entity<ResearchTopic>(topic =>
        {
            topic.Property(item => item.Question).HasMaxLength(ResearchTopic.MaxQuestionLength).IsRequired();
            topic.Property(item => item.Notes).HasMaxLength(ResearchTopic.MaxNotesLength);
            topic.HasIndex(item => item.OwnerId);
            topic.HasMany(item => item.Entries).WithOne(item => item.Topic).HasForeignKey(item => item.TopicId).OnDelete(DeleteBehavior.Cascade);
            topic.HasOne<Reader>().WithMany().HasForeignKey(item => item.OwnerId).OnDelete(DeleteBehavior.Cascade);
            topic.HasQueryFilter(item => item.OwnerId == ReaderId);
        });

        // A note outlives the book it cites, as the reader's own words.
        modelBuilder.Entity<ResearchNote>(note =>
        {
            note.Property(item => item.Place).HasMaxLength(ResearchNote.MaxPlaceLength);
            note.Property(item => item.Quote).HasMaxLength(ResearchNote.MaxQuoteLength);
            note.Property(item => item.Text).HasMaxLength(ResearchNote.MaxTextLength);
            note.Property(item => item.Link).HasMaxLength(ResearchNote.MaxLinkLength);
            note.HasIndex(item => item.TopicId);
            note.HasIndex(item => item.BookId);
            note.HasOne(item => item.Book).WithMany().HasForeignKey(item => item.BookId).OnDelete(DeleteBehavior.SetNull);
            note.HasQueryFilter(item => item.Topic!.OwnerId == ReaderId);
        });

        modelBuilder.Entity<VocabularyWord>(word =>
        {
            word.Property(item => item.Word).HasMaxLength(VocabularyWord.MaxWordLength).IsRequired();
            word.Property(item => item.Language).HasMaxLength(VocabularyWord.MaxLanguageLength).IsRequired();
            word.Property(item => item.Meaning).HasMaxLength(VocabularyWord.MaxMeaningLength);
            word.Property(item => item.Context).HasMaxLength(VocabularyWord.MaxContextLength);
            word.Property(item => item.Note).HasMaxLength(VocabularyWord.MaxNoteLength);
            word.HasIndex(item => new { item.OwnerId, item.Language, item.Word }).IsUnique();
            word.HasIndex(item => new { item.OwnerId, item.DueOn });
            word.HasOne(item => item.Book).WithMany().HasForeignKey(item => item.BookId).OnDelete(DeleteBehavior.SetNull);
            word.HasOne<Reader>().WithMany().HasForeignKey(item => item.OwnerId).OnDelete(DeleteBehavior.Cascade);
            word.HasQueryFilter(item => item.OwnerId == ReaderId);
        });

        modelBuilder.Entity<ParallelPassage>(passage =>
        {
            passage.Property(item => item.Original).HasMaxLength(ParallelPassage.MaxTextLength).IsRequired();
            passage.Property(item => item.Translation).HasMaxLength(ParallelPassage.MaxTextLength).IsRequired();
            passage.Property(item => item.OriginalLanguage).HasMaxLength(40);
            passage.Property(item => item.TranslationLanguage).HasMaxLength(40);
            passage.Property(item => item.Place).HasMaxLength(40);
            passage.Property(item => item.Note).HasMaxLength(1000);
            passage.HasIndex(item => item.OwnerId);
            passage.HasOne(item => item.Book).WithMany().HasForeignKey(item => item.BookId).OnDelete(DeleteBehavior.SetNull);
            passage.HasOne<Book>().WithMany().HasForeignKey(item => item.OtherBookId).OnDelete(DeleteBehavior.SetNull);
            passage.HasOne<Reader>().WithMany().HasForeignKey(item => item.OwnerId).OnDelete(DeleteBehavior.Cascade);
            passage.HasQueryFilter(item => item.OwnerId == ReaderId);
        });

        modelBuilder.Entity<DetailProposal>(proposal =>
        {
            proposal.Property(item => item.Field).HasMaxLength(24).IsRequired();
            proposal.Property(item => item.Current).HasMaxLength(500);
            proposal.Property(item => item.OpenLibrary).HasMaxLength(500);
            proposal.Property(item => item.Google).HasMaxLength(500);
            proposal.HasIndex(item => new { item.OwnerId, item.BookId });
            proposal.HasOne(item => item.Book).WithMany().HasForeignKey(item => item.BookId).OnDelete(DeleteBehavior.Cascade);
            proposal.HasOne<Reader>().WithMany().HasForeignKey(item => item.OwnerId).OnDelete(DeleteBehavior.Cascade);
            proposal.HasQueryFilter(item => item.OwnerId == ReaderId);
        });

        // Reached by reader id, as the event handlers write every concerned reader's.
        modelBuilder.Entity<EventRecord>(record =>
        {
            record.Property(item => item.Kind).HasMaxLength(40).IsRequired();
            record.Property(item => item.Payload).HasMaxLength(8000).IsRequired();
            record.HasIndex(item => new { item.ReaderId, item.Id });
            record.HasOne<Reader>().WithMany().HasForeignKey(item => item.ReaderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReadingGoal>(goal =>
        {
            goal.Property(item => item.Kind).HasConversion<string>().HasMaxLength(16);
            goal.Property(item => item.Name).HasMaxLength(ReadingGoal.MaxNameLength);
            goal.HasIndex(item => new { item.OwnerId, item.Year });
            goal.HasOne<Reader>().WithMany().HasForeignKey(item => item.OwnerId).OnDelete(DeleteBehavior.Cascade);
            goal.HasQueryFilter(item => item.OwnerId == ReaderId);
        });

        modelBuilder.Entity<CopyPhoto>(photo =>
        {
            photo.Property(item => item.StoredName).HasMaxLength(48).IsRequired();
            photo.Property(item => item.Caption).HasMaxLength(CopyPhoto.MaxCaptionLength);
            photo.HasOne(item => item.Book).WithMany().HasForeignKey(item => item.BookId).OnDelete(DeleteBehavior.Cascade);
            photo.HasIndex(item => item.BookId);
            photo.HasQueryFilter(item => item.Book!.OwnerId == ReaderId);
        });

        // Like clubs, reached through membership in ClubMeetings rather than a filter.
        modelBuilder.Entity<ClubMeeting>(meeting =>
        {
            meeting.Property(item => item.Through).HasMaxLength(ClubMeeting.MaxThroughLength);
            meeting.Property(item => item.Agenda).HasMaxLength(ClubMeeting.MaxAgendaLength);
            meeting.Property(item => item.Summary).HasMaxLength(ClubMeeting.MaxSummaryLength);
            meeting.Property(item => item.TimeZone).HasMaxLength(ClubMeeting.MaxTimeZoneLength);
            meeting.HasOne(item => item.Club).WithMany().HasForeignKey(item => item.ClubId).OnDelete(DeleteBehavior.Cascade);
            meeting.HasOne(item => item.Book).WithMany().HasForeignKey(item => item.ClubBookId).OnDelete(DeleteBehavior.Cascade);
            meeting.HasMany(item => item.Questions).WithOne(question => question.Meeting).HasForeignKey(question => question.MeetingId).OnDelete(DeleteBehavior.Cascade);
            meeting.HasIndex(item => item.ClubId);
        });

        modelBuilder.Entity<ClubQuestion>(question =>
        {
            question.Property(item => item.Text).HasMaxLength(ClubQuestion.MaxLength).IsRequired();
            question.HasOne<Reader>().WithMany().HasForeignKey(item => item.ReaderId).OnDelete(DeleteBehavior.Cascade);
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

        // Audiobookshelf items brought across, and the library kept in step. Reached by reader id, not filtered, so
        // the keeper can look at every reader's.
        modelBuilder.Entity<AbsLink>(link =>
        {
            link.Property(item => item.Server).HasMaxLength(500);
            link.Property(item => item.ItemId).HasMaxLength(100);
            link.HasIndex(item => new { item.ReaderId, item.Server, item.ItemId }).IsUnique();
            link.HasOne<Reader>().WithMany().HasForeignKey(item => item.ReaderId).OnDelete(DeleteBehavior.Cascade);
            link.HasOne<Book>().WithMany().HasForeignKey(item => item.BookId).OnDelete(DeleteBehavior.Cascade);
        });

        // Finding and fetching books: the shelf's one setting, and each download, kept by reader id for the fetcher.
        modelBuilder.Entity<FetchSetting>(fetch =>
        {
            foreach (var name in new[] { nameof(FetchSetting.IndexerUrl), nameof(FetchSetting.TorrentUrl), nameof(FetchSetting.SabUrl), nameof(FetchSetting.RemotePath), nameof(FetchSetting.LocalPath) })
            {
                fetch.Property(name).HasMaxLength(500);
            }

            foreach (var name in new[] { nameof(FetchSetting.SealedIndexerKey), nameof(FetchSetting.SealedTorrentPassword), nameof(FetchSetting.SealedSabKey) })
            {
                fetch.Property(name).HasMaxLength(4000);
            }

            fetch.Property(item => item.TorrentUser).HasMaxLength(200);
            fetch.Property(item => item.Category).HasMaxLength(100);
            fetch.Property(item => item.EbookFormats).HasMaxLength(200);
            fetch.Property(item => item.AudioFormats).HasMaxLength(200);
        });

        // Authors followed, and their new books waiting; by reader id, for the watcher to reach every reader's.
        modelBuilder.Entity<AuthorFollow>(follow =>
        {
            follow.Property(item => item.Name).HasMaxLength(200);
            follow.HasIndex(item => item.ReaderId);
            follow.HasOne<Reader>().WithMany().HasForeignKey(item => item.ReaderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuthorAlert>(alert =>
        {
            alert.Property(item => item.Key).HasMaxLength(100);
            alert.Property(item => item.Author).HasMaxLength(200);
            alert.Property(item => item.Title).HasMaxLength(200);
            alert.Property(item => item.Isbn).HasMaxLength(20);
            alert.Property(item => item.CoverUrl).HasMaxLength(500);
            alert.HasIndex(item => new { item.ReaderId, item.Key });
            alert.HasOne<Reader>().WithMany().HasForeignKey(item => item.ReaderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FetchGrab>(grab =>
        {
            grab.Property(item => item.Title).HasMaxLength(500);
            grab.Property(item => item.Indexer).HasMaxLength(200);
            grab.Property(item => item.Protocol).HasMaxLength(20);
            grab.Property(item => item.DownloadId).HasMaxLength(200);
            grab.Property(item => item.Path).HasMaxLength(2000);
            grab.Property(item => item.Problem).HasMaxLength(500);
            grab.HasIndex(item => new { item.ReaderId, item.BookId });
            grab.HasIndex(item => item.State);
            grab.HasOne<Reader>().WithMany().HasForeignKey(item => item.ReaderId).OnDelete(DeleteBehavior.Cascade);
            grab.HasOne<Book>().WithMany().HasForeignKey(item => item.BookId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AbsSyncSetting>(sync =>
        {
            sync.Property(item => item.Server).HasMaxLength(500);
            sync.Property(item => item.LibraryId).HasMaxLength(100);
            sync.Property(item => item.LibraryName).HasMaxLength(200);
            sync.Property(item => item.SealedKey).HasMaxLength(4000);
            sync.Property(item => item.Problem).HasMaxLength(500);
            sync.HasIndex(item => item.ReaderId).IsUnique();
            sync.HasOne<Reader>().WithMany().HasForeignKey(item => item.ReaderId).OnDelete(DeleteBehavior.Cascade);
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
            entry.Property(item => item.Note).HasMaxLength(CollectionBook.MaxNoteLength);
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
            setting.Property(s => s.HomeSections).HasMaxLength(200);
        });

        modelBuilder.Entity<Reader>(reader =>
        {
            reader.HasOne<Reader>().WithMany().HasForeignKey(r => r.GuardianId).OnDelete(DeleteBehavior.SetNull);
            reader.HasOne<Reader>().WithMany().HasForeignKey(r => r.SandboxOf).OnDelete(DeleteBehavior.Restrict);
            reader.Property(r => r.Name).HasMaxLength(ReaderRules.MaxNameLength).IsRequired();
            reader.Property(r => r.NormalizedName).HasMaxLength(ReaderRules.MaxNameLength).IsRequired();
            reader.Property(r => r.PasswordHash).HasMaxLength(200).IsRequired();
            reader.Property(r => r.KeyHash).HasMaxLength(64);
            reader.HasIndex(r => r.NormalizedName);
            reader.Property(r => r.Stamp).HasMaxLength(64).IsRequired();
            reader.Property(r => r.Email).HasMaxLength(EmailRules.MaxAddressLength);
            reader.Property(r => r.NormalizedEmail).HasMaxLength(EmailRules.MaxAddressLength);
            reader.HasIndex(r => r.NormalizedEmail).IsUnique();
            reader.Property(r => r.PendingEmail).HasMaxLength(EmailRules.MaxAddressLength);
            reader.Property(r => r.PendingNormalizedEmail).HasMaxLength(EmailRules.MaxAddressLength);
            reader.HasIndex(r => r.PendingNormalizedEmail);
            reader.Property(r => r.PendingEmailTokenHash).HasMaxLength(64);
            reader.HasIndex(r => r.PendingEmailTokenHash).IsUnique();
            reader.Property(r => r.KnownDevices).HasMaxLength(2000);
            reader.Property(r => r.HealthTold).HasMaxLength(64);
            reader.Property(r => r.TwoFactorSecret).HasMaxLength(500);
            reader.Property(r => r.Culture).HasMaxLength(16);
            reader.HasIndex(r => r.KeyHash).IsUnique();
            reader.Property(r => r.OidcSubject).HasMaxLength(600);
            reader.Property(r => r.KindleEmail).HasMaxLength(EmailRules.MaxAddressLength);
            reader.Property(r => r.ReadwiseToken).HasMaxLength(1000);
            reader.Property(r => r.HardcoverToken).HasMaxLength(6000);
            reader.Property(r => r.ReadwiseProblem).HasConversion<string>().HasMaxLength(20);
            reader.Property(r => r.HardcoverProblem).HasConversion<string>().HasMaxLength(20);
            reader.Property(r => r.NtfyUrl).HasMaxLength(500);
            reader.Property(r => r.CalendarTokenHash).HasMaxLength(64);
            reader.Property(r => r.PublicToken).HasMaxLength(40);
            reader.Property(r => r.DismissedRelease).HasMaxLength(32);
            reader.Property(r => r.HiddenNav).HasMaxLength(4000);
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
            token.Property(item => item.Areas).HasMaxLength(200);
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

        modelBuilder.Entity<SiteSetting>(site =>
        {
            site.Property(item => item.Id).ValueGeneratedNever();
            site.Property(item => item.SignUp).HasConversion<string>().HasMaxLength(16);
            site.Property(item => item.Announcement).HasMaxLength(SiteSetting.MaxAnnouncementLength);
        });

        modelBuilder.Entity<Invitation>(invitation =>
        {
            invitation.Property(item => item.Email).HasMaxLength(EmailRules.MaxAddressLength).IsRequired();
            invitation.Property(item => item.NormalizedEmail).HasMaxLength(EmailRules.MaxAddressLength).IsRequired();
            invitation.HasIndex(item => item.NormalizedEmail);
            invitation.Property(item => item.TokenHash).HasMaxLength(64).IsRequired();
            invitation.HasIndex(item => item.TokenHash);
            invitation.HasOne<Reader>().WithMany().HasForeignKey(item => item.InvitedById).OnDelete(DeleteBehavior.Cascade);
            invitation.HasOne<Reader>().WithMany().HasForeignKey(item => item.ReaderId).OnDelete(DeleteBehavior.SetNull);
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
            place.Property(item => item.EbookCfi).HasMaxLength(1000);
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

            // A finished book begun again, its read put by, is read again rather than started.
            var status = entry.Property(item => item.Status);
            if (status.IsModified && !Equals(status.OriginalValue, status.CurrentValue) && book.Status is BookStatus.Reading or BookStatus.Finished)
            {
                var again = book.Status == BookStatus.Reading && status.OriginalValue == BookStatus.Finished
                    && ChangeTracker.Entries<PastRead>().Any(read => read.State == EntityState.Added && read.Entity.BookId == book.Id);
                var kind = book.Status == BookStatus.Finished ? EventKinds.BookFinished : again ? EventKinds.BookReread : EventKinds.BookStarted;
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

        foreach (var meeting in ChangeTracker.Entries<ClubMeeting>().Where(entry => entry.State == EntityState.Added).Select(entry => entry.Entity))
        {
            _happening.Add(() => new ShelfEvent(EventKinds.ClubMeeting, actor, ClubId: meeting.ClubId, MeetingId: meeting.Id));
        }

        foreach (var member in ChangeTracker.Entries<ClubMember>().Where(entry => entry.State == EntityState.Added).Select(entry => entry.Entity))
        {
            _happening.Add(() => new ShelfEvent(EventKinds.ClubJoined, actor, ReaderId: member.ReaderId, ClubId: member.ClubId));
        }

        // A club's next book, a meeting called off, a member let go, and a club ended. A club going takes its members and
        // meetings with it, which are its ending, not each a change of its own.
        var endedClubs = ChangeTracker.Entries<Club>().Where(entry => entry.State == EntityState.Deleted).Select(entry => entry.Entity).ToList();
        var goneReaders = ChangeTracker.Entries<Reader>().Where(entry => entry.State == EntityState.Deleted).Select(entry => entry.Entity.Id).ToHashSet();
        foreach (var entry in ChangeTracker.Entries<Club>().Where(entry => entry.State == EntityState.Modified))
        {
            var current = entry.Property(item => item.CurrentBookId);
            if (current.IsModified && current.CurrentValue is int picked && current.OriginalValue != picked)
            {
                var clubId = entry.Entity.Id;
                _happening.Add(() => new ShelfEvent(EventKinds.ClubReading, actor, ClubId: clubId, ClubBookId: picked));
            }
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var meeting in ChangeTracker.Entries<ClubMeeting>().Where(entry => entry.State == EntityState.Deleted).Select(entry => entry.Entity))
        {
            if (meeting.At > now && endedClubs.All(club => club.Id != meeting.ClubId))
            {
                _happening.Add(() => new ShelfEvent(EventKinds.ClubMeetingCancelled, actor, ClubId: meeting.ClubId, ClubBookId: meeting.ClubBookId, When: meeting.At, Zone: meeting.TimeZone));
            }
        }

        foreach (var member in ChangeTracker.Entries<ClubMember>().Where(entry => entry.State == EntityState.Deleted).Select(entry => entry.Entity))
        {
            if (member.ReaderId == actor || goneReaders.Contains(member.ReaderId))
            {
                continue;
            }

            var (readerId, clubId) = (member.ReaderId, member.ClubId);
            _happening.Add(endedClubs.FirstOrDefault(club => club.Id == clubId) is { } ended
                ? () => new ShelfEvent(EventKinds.ClubEnded, actor, ReaderId: readerId, Name: ended.Name)
                : () => new ShelfEvent(EventKinds.ClubLeft, actor, ReaderId: readerId, ClubId: clubId));
        }

        // An ask let go by the book's owner, not by lending it to the one who asked.
        foreach (var ask in ChangeTracker.Entries<LoanAskRow>().Where(entry => entry.State == EntityState.Deleted).Select(entry => entry.Entity))
        {
            var lent = ChangeTracker.Entries<Book>().Any(entry => entry.Entity.Id == ask.BookId && (entry.Entity.BorrowerId == ask.ReaderId || entry.State == EntityState.Deleted));
            if (ask.ReaderId != actor && !lent && !goneReaders.Contains(ask.ReaderId))
            {
                var (bookId, asker) = (ask.BookId, ask.ReaderId);
                _happening.Add(() => new ShelfEvent(EventKinds.AskDeclined, actor, bookId, asker));
            }
        }

        // A new reader, for the admins; and changes to how a reader signs in, for that reader.
        foreach (var entry in ChangeTracker.Entries<Reader>())
        {
            var reader = entry.Entity;
            if (entry.State == EntityState.Added && reader.SandboxOf is null)
            {
                _happening.Add(() => new ShelfEvent(EventKinds.ReaderJoined, actor, ReaderId: reader.Id));
                continue;
            }

            if (entry.State != EntityState.Modified)
            {
                continue;
            }

            var readerId = reader.Id;
            var email = entry.Property(item => item.Email);
            if (email.IsModified && email.OriginalValue is { } before && !string.Equals(before, email.CurrentValue, StringComparison.OrdinalIgnoreCase))
            {
                _happening.Add(() => new ShelfEvent(EventKinds.EmailChanged, actor, ReaderId: readerId, Name: before));
            }

            // A new password comes with a new stamp; a new stamp alone (signed out everywhere, or suspended) is not one.
            var stamp = entry.Property(item => item.Stamp);
            if (stamp.IsModified && stamp.OriginalValue != stamp.CurrentValue && entry.Property(item => item.PasswordHash).IsModified)
            {
                _happening.Add(() => new ShelfEvent(EventKinds.PasswordChanged, actor, ReaderId: readerId));
            }

            var twoStep = entry.Property(item => item.TwoFactorEnabled);
            if (twoStep.IsModified && twoStep.OriginalValue && !twoStep.CurrentValue)
            {
                _happening.Add(() => new ShelfEvent(EventKinds.TwoStepOff, actor, ReaderId: readerId));
            }
        }

        foreach (var passkey in ChangeTracker.Entries<ReaderPasskey>().Where(entry => entry.State == EntityState.Added).Select(entry => entry.Entity))
        {
            _happening.Add(() => new ShelfEvent(EventKinds.PasskeyAdded, actor, ReaderId: passkey.ReaderId, Name: passkey.Name));
        }

        foreach (var session in ChangeTracker.Entries<ReaderSession>().Where(entry => entry.State == EntityState.Added).Select(entry => entry.Entity))
        {
            _happening.Add(() => new ShelfEvent(EventKinds.SignedIn, session.ReaderId, ReaderId: session.ReaderId, Name: session.Device));
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

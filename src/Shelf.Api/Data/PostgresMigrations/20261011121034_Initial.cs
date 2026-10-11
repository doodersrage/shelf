using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Shelf.Api.Data.PostgresMigrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorId = table.Column<int>(type: "integer", nullable: true),
                    ActorName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Action = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TargetId = table.Column<int>(type: "integer", nullable: true),
                    TargetName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Detail = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Readers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    KeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    KosyncHash = table.Column<string>(type: "text", nullable: true),
                    OidcSubject = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: true),
                    PasswordUnknown = table.Column<bool>(type: "boolean", nullable: false),
                    PasskeyHandle = table.Column<byte[]>(type: "bytea", nullable: true),
                    Language = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsAdmin = table.Column<bool>(type: "boolean", nullable: false),
                    Restricted = table.Column<bool>(type: "boolean", nullable: false),
                    GuardianId = table.Column<int>(type: "integer", nullable: true),
                    ShelfOpen = table.Column<bool>(type: "boolean", nullable: false),
                    Stamp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    EmailReminders = table.Column<bool>(type: "boolean", nullable: false),
                    LastReminderOn = table.Column<DateOnly>(type: "date", nullable: true),
                    KindleEmail = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    ReadwiseToken = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ReadwiseAuto = table.Column<bool>(type: "boolean", nullable: false),
                    ReadwiseSentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReadwiseAutoImport = table.Column<bool>(type: "boolean", nullable: false),
                    ReadwiseImportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HardcoverToken = table.Column<string>(type: "character varying(6000)", maxLength: 6000, nullable: true),
                    HardcoverAuto = table.Column<bool>(type: "boolean", nullable: false),
                    HardcoverAddMissing = table.Column<bool>(type: "boolean", nullable: false),
                    KoboTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    KoboCollectionId = table.Column<int>(type: "integer", nullable: true),
                    NoticeKinds = table.Column<int>(type: "integer", nullable: false),
                    NtfyUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    NtfyToken = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    GotifyUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    GotifyToken = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    LastNoticeOn = table.Column<DateOnly>(type: "date", nullable: true),
                    StorageLimitMegabytes = table.Column<int>(type: "integer", nullable: true),
                    CalendarTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PublicToken = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorSecret = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Culture = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Readers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Readers_Readers_GuardianId",
                        column: x => x.GuardianId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    YearlyGoal = table.Column<int>(type: "integer", nullable: false),
                    SyncAddress = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    SyncKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ReaderTextSize = table.Column<int>(type: "integer", nullable: true),
                    ReaderLineHeight = table.Column<int>(type: "integer", nullable: true),
                    ReaderWidth = table.Column<int>(type: "integer", nullable: true),
                    PagesGoal = table.Column<int>(type: "integer", nullable: false),
                    HoursGoal = table.Column<int>(type: "integer", nullable: false),
                    ReaderPaged = table.Column<bool>(type: "boolean", nullable: false),
                    ComicSpread = table.Column<bool>(type: "boolean", nullable: false),
                    LibraryAsList = table.Column<bool>(type: "boolean", nullable: false),
                    AudioSpeed = table.Column<int>(type: "integer", nullable: true),
                    WatchSeries = table.Column<bool>(type: "boolean", nullable: false),
                    SeriesCheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShelfLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TheirSecret = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    OurSecretHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    InviteHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    InviteExpires = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShelfLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiTokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReaderId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CanChange = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiTokens_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Clubs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CurrentBookId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clubs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Clubs_Readers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Collections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ShareToken = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ForChildren = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Collections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Collections_Readers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KoboEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReaderId = table.Column<int>(type: "integer", nullable: false),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    Archived = table.Column<bool>(type: "boolean", nullable: false),
                    Bookmark = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    SyncedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KoboEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KoboEntries_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PasswordResets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReaderId = table.Column<int>(type: "integer", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Used = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasswordResets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PasswordResets_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Podcasts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerId = table.Column<int>(type: "integer", nullable: false),
                    FeedUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Author = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ImageUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    KeepLatest = table.Column<int>(type: "integer", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Problem = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Podcasts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Podcasts_Readers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PushSubscriptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReaderId = table.Column<int>(type: "integer", nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    P256dh = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Auth = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PushSubscriptions_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReaderPasskeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReaderId = table.Column<int>(type: "integer", nullable: false),
                    CredentialId = table.Column<byte[]>(type: "bytea", maxLength: 1024, nullable: false),
                    PublicKey = table.Column<byte[]>(type: "bytea", nullable: false),
                    SignCount = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReaderPasskeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReaderPasskeys_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReaderSessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReaderId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Device = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReaderSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReaderSessions_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecoveryCodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReaderId = table.Column<int>(type: "integer", nullable: false),
                    CodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Used = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecoveryCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecoveryCodes_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SavedSearches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Query = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ShareToken = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedSearches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SavedSearches_Readers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SeriesAlerts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerId = table.Column<int>(type: "integer", nullable: false),
                    Series = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Author = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: true),
                    WorkKey = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    FoundAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Dismissed = table.Column<bool>(type: "boolean", nullable: false),
                    Mailed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesAlerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeriesAlerts_Readers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Webhooks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReaderId = table.Column<int>(type: "integer", nullable: false),
                    Url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Format = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Events = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Secret = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastStatus = table.Column<int>(type: "integer", nullable: true),
                    Failures = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Webhooks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Webhooks_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LinkedAsks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkId = table.Column<int>(type: "integer", nullable: false),
                    ReaderId = table.Column<int>(type: "integer", nullable: false),
                    RemoteBookId = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Author = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Owner = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    AskedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkedAsks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LinkedAsks_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LinkedAsks_ShelfLinks_LinkId",
                        column: x => x.LinkId,
                        principalTable: "ShelfLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RemoteReaders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkId = table.Column<int>(type: "integer", nullable: false),
                    RemoteId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteReaders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RemoteReaders_ShelfLinks_LinkId",
                        column: x => x.LinkId,
                        principalTable: "ShelfLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClubBooks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClubId = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Author = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CoverUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Isbn = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: true),
                    AddedById = table.Column<int>(type: "integer", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClubBooks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClubBooks_Clubs_ClubId",
                        column: x => x.ClubId,
                        principalTable: "Clubs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClubMembers",
                columns: table => new
                {
                    ClubId = table.Column<int>(type: "integer", nullable: false),
                    ReaderId = table.Column<int>(type: "integer", nullable: false),
                    JoinedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClubMembers", x => new { x.ClubId, x.ReaderId });
                    table.ForeignKey(
                        name: "FK_ClubMembers_Clubs_ClubId",
                        column: x => x.ClubId,
                        principalTable: "Clubs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClubMembers_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PodcastEpisodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PodcastId = table.Column<int>(type: "integer", nullable: false),
                    Guid = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Summary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AudioUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: true),
                    StoredName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Seconds = table.Column<double>(type: "double precision", nullable: false),
                    Played = table.Column<bool>(type: "boolean", nullable: false),
                    PlayedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FoundAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PodcastEpisodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PodcastEpisodes_Podcasts_PodcastId",
                        column: x => x.PodcastId,
                        principalTable: "Podcasts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Books",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerId = table.Column<int>(type: "integer", nullable: true),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Author = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Rating = table.Column<int>(type: "integer", nullable: true),
                    Year = table.Column<int>(type: "integer", nullable: true),
                    Isbn = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: true),
                    Pages = table.Column<int>(type: "integer", nullable: true),
                    CurrentPage = table.Column<int>(type: "integer", nullable: true),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    StartedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    FinishedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    LoanedTo = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    LoanedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    DueOn = table.Column<DateOnly>(type: "date", nullable: true),
                    BorrowerId = table.Column<int>(type: "integer", nullable: true),
                    Subtitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Publisher = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Language = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Format = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Series = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SeriesNumber = table.Column<int>(type: "integer", nullable: true),
                    CoverUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FileCover = table.Column<bool>(type: "boolean", nullable: false),
                    CoverImage = table.Column<string>(type: "text", nullable: true),
                    KoreaderDigest = table.Column<string>(type: "text", nullable: true),
                    Review = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Loved = table.Column<bool>(type: "boolean", nullable: false),
                    Queued = table.Column<bool>(type: "boolean", nullable: false),
                    Location = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    AcquiredOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Acquisition = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Condition = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    EbookFileName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    EbookStoredName = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    EbookChapter = table.Column<int>(type: "integer", nullable: true),
                    AudioFileName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AudioStoredName = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    AudioTrack = table.Column<int>(type: "integer", nullable: true),
                    AudioSeconds = table.Column<int>(type: "integer", nullable: true),
                    Translator = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Narrator = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ComicRightToLeft = table.Column<bool>(type: "boolean", nullable: true),
                    Asin = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    AudioChapterNames = table.Column<string>(type: "text", nullable: true),
                    RemoteBorrowerId = table.Column<int>(type: "integer", nullable: true),
                    VisitingLinkId = table.Column<int>(type: "integer", nullable: true),
                    VisitingBookId = table.Column<int>(type: "integer", nullable: true),
                    VisitingOwner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    VisitingDueOn = table.Column<DateOnly>(type: "date", nullable: true),
                    HardcoverBookId = table.Column<int>(type: "integer", nullable: true),
                    HardcoverUserBookId = table.Column<int>(type: "integer", nullable: true),
                    HardcoverState = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    OriginalTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Inscription = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RecommendedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Books", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Books_Readers_BorrowerId",
                        column: x => x.BorrowerId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Books_Readers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Books_RemoteReaders_RemoteBorrowerId",
                        column: x => x.RemoteBorrowerId,
                        principalTable: "RemoteReaders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Books_ShelfLinks_VisitingLinkId",
                        column: x => x.VisitingLinkId,
                        principalTable: "ShelfLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ClubPosts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClubBookId = table.Column<int>(type: "integer", nullable: false),
                    ReaderId = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    PostedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClubPosts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClubPosts_ClubBooks_ClubBookId",
                        column: x => x.ClubBookId,
                        principalTable: "ClubBooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClubPosts_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AudioBookmarks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    ReaderId = table.Column<int>(type: "integer", nullable: true),
                    Track = table.Column<int>(type: "integer", nullable: false),
                    Seconds = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AudioBookmarks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AudioBookmarks_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AudioBookmarks_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BookTags",
                columns: table => new
                {
                    BooksId = table.Column<int>(type: "integer", nullable: false),
                    TagsId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookTags", x => new { x.BooksId, x.TagsId });
                    table.ForeignKey(
                        name: "FK_BookTags_Books_BooksId",
                        column: x => x.BooksId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BookTags_Tags_TagsId",
                        column: x => x.TagsId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CollectionBooks",
                columns: table => new
                {
                    CollectionId = table.Column<int>(type: "integer", nullable: false),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionBooks", x => new { x.CollectionId, x.BookId });
                    table.ForeignKey(
                        name: "FK_CollectionBooks_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CollectionBooks_Collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "Collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Highlights",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    ReaderId = table.Column<int>(type: "integer", nullable: true),
                    ChapterIndex = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Prefix = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Suffix = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    NotedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Highlights", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Highlights_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Highlights_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KosyncPlaces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReaderId = table.Column<int>(type: "integer", nullable: false),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    Progress = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Percentage = table.Column<double>(type: "double precision", nullable: false),
                    Chapter = table.Column<int>(type: "integer", nullable: false),
                    Device = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DeviceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KosyncPlaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KosyncPlaces_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KosyncPlaces_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ListeningDays",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    ReaderId = table.Column<int>(type: "integer", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Seconds = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListeningDays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ListeningDays_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ListeningDays_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LoanAsks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    ReaderId = table.Column<int>(type: "integer", nullable: false),
                    AskedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanAsks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoanAsks_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LoanAsks_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LoanPlaces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    ReaderId = table.Column<int>(type: "integer", nullable: false),
                    EbookChapter = table.Column<int>(type: "integer", nullable: true),
                    AudioTrack = table.Column<int>(type: "integer", nullable: true),
                    AudioSeconds = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanPlaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoanPlaces_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LoanPlaces_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OcrScans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    StoredName = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Pages = table.Column<int>(type: "integer", nullable: false),
                    Done = table.Column<int>(type: "integer", nullable: false),
                    Failed = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OcrScans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OcrScans_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PartSummaries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Part = table.Column<int>(type: "integer", nullable: false),
                    Language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartSummaries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartSummaries_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Quotes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Page = table.Column<int>(type: "integer", nullable: true),
                    NotedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Quotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Quotes_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReadAlongs",
                columns: table => new
                {
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Done = table.Column<int>(type: "integer", nullable: false),
                    Total = table.Column<int>(type: "integer", nullable: false),
                    Problem = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AudioStoredName = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    EbookStoredName = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    Transcript = table.Column<byte[]>(type: "bytea", nullable: true),
                    Timings = table.Column<byte[]>(type: "bytea", nullable: true),
                    Matched = table.Column<double>(type: "double precision", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReadAlongs", x => x.BookId);
                    table.ForeignKey(
                        name: "FK_ReadAlongs_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReadingDays",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    ReaderId = table.Column<int>(type: "integer", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Seconds = table.Column<int>(type: "integer", nullable: false),
                    DeviceSeconds = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReadingDays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReadingDays_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ReadingDays_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RemoteAsks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    RemoteReaderId = table.Column<int>(type: "integer", nullable: false),
                    AskedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteAsks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RemoteAsks_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RemoteAsks_RemoteReaders_RemoteReaderId",
                        column: x => x.RemoteReaderId,
                        principalTable: "RemoteReaders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    FromPage = table.Column<int>(type: "integer", nullable: true),
                    ToPage = table.Column<int>(type: "integer", nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sessions_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SpokenTexts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    AudioStoredName = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Start = table.Column<double>(type: "double precision", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpokenTexts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpokenTexts_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BookTexts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ScanId = table.Column<int>(type: "integer", nullable: false),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    Part = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookTexts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookTexts_OcrScans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "OcrScans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OcrPages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ScanId = table.Column<int>(type: "integer", nullable: false),
                    Page = table.Column<int>(type: "integer", nullable: false),
                    Words = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OcrPages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OcrPages_OcrScans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "OcrScans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiTokens_Hash",
                table: "ApiTokens",
                column: "Hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiTokens_ReaderId",
                table: "ApiTokens",
                column: "ReaderId");

            migrationBuilder.CreateIndex(
                name: "IX_AudioBookmarks_BookId",
                table: "AudioBookmarks",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_AudioBookmarks_ReaderId",
                table: "AudioBookmarks",
                column: "ReaderId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_ActorId",
                table: "AuditEntries",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_TargetId",
                table: "AuditEntries",
                column: "TargetId");

            migrationBuilder.CreateIndex(
                name: "IX_Books_Author",
                table: "Books",
                column: "Author");

            migrationBuilder.CreateIndex(
                name: "IX_Books_BorrowerId",
                table: "Books",
                column: "BorrowerId");

            migrationBuilder.CreateIndex(
                name: "IX_Books_OwnerId",
                table: "Books",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Books_RemoteBorrowerId",
                table: "Books",
                column: "RemoteBorrowerId");

            migrationBuilder.CreateIndex(
                name: "IX_Books_Status",
                table: "Books",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Books_VisitingLinkId",
                table: "Books",
                column: "VisitingLinkId");

            migrationBuilder.CreateIndex(
                name: "IX_BookTags_TagsId",
                table: "BookTags",
                column: "TagsId");

            migrationBuilder.CreateIndex(
                name: "IX_BookTexts_BookId_Part",
                table: "BookTexts",
                columns: new[] { "BookId", "Part" });

            migrationBuilder.CreateIndex(
                name: "IX_BookTexts_ScanId",
                table: "BookTexts",
                column: "ScanId");

            migrationBuilder.CreateIndex(
                name: "IX_ClubBooks_ClubId",
                table: "ClubBooks",
                column: "ClubId");

            migrationBuilder.CreateIndex(
                name: "IX_ClubMembers_ReaderId",
                table: "ClubMembers",
                column: "ReaderId");

            migrationBuilder.CreateIndex(
                name: "IX_ClubPosts_ClubBookId",
                table: "ClubPosts",
                column: "ClubBookId");

            migrationBuilder.CreateIndex(
                name: "IX_ClubPosts_ReaderId",
                table: "ClubPosts",
                column: "ReaderId");

            migrationBuilder.CreateIndex(
                name: "IX_Clubs_OwnerId",
                table: "Clubs",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_CollectionBooks_BookId",
                table: "CollectionBooks",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_Collections_OwnerId",
                table: "Collections",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Collections_ShareToken",
                table: "Collections",
                column: "ShareToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Highlights_BookId",
                table: "Highlights",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_Highlights_ReaderId",
                table: "Highlights",
                column: "ReaderId");

            migrationBuilder.CreateIndex(
                name: "IX_KoboEntries_ReaderId_BookId",
                table: "KoboEntries",
                columns: new[] { "ReaderId", "BookId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KosyncPlaces_BookId",
                table: "KosyncPlaces",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_KosyncPlaces_ReaderId_BookId",
                table: "KosyncPlaces",
                columns: new[] { "ReaderId", "BookId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LinkedAsks_LinkId",
                table: "LinkedAsks",
                column: "LinkId");

            migrationBuilder.CreateIndex(
                name: "IX_LinkedAsks_ReaderId_LinkId_RemoteBookId",
                table: "LinkedAsks",
                columns: new[] { "ReaderId", "LinkId", "RemoteBookId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ListeningDays_BookId",
                table: "ListeningDays",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_ListeningDays_ReaderId_BookId_Day",
                table: "ListeningDays",
                columns: new[] { "ReaderId", "BookId", "Day" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoanAsks_BookId_ReaderId",
                table: "LoanAsks",
                columns: new[] { "BookId", "ReaderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoanAsks_ReaderId",
                table: "LoanAsks",
                column: "ReaderId");

            migrationBuilder.CreateIndex(
                name: "IX_LoanPlaces_BookId_ReaderId",
                table: "LoanPlaces",
                columns: new[] { "BookId", "ReaderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoanPlaces_ReaderId",
                table: "LoanPlaces",
                column: "ReaderId");

            migrationBuilder.CreateIndex(
                name: "IX_OcrPages_ScanId_Page",
                table: "OcrPages",
                columns: new[] { "ScanId", "Page" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OcrScans_BookId_StoredName",
                table: "OcrScans",
                columns: new[] { "BookId", "StoredName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PartSummaries_BookId_Source_Language_Part",
                table: "PartSummaries",
                columns: new[] { "BookId", "Source", "Language", "Part" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PasswordResets_ReaderId",
                table: "PasswordResets",
                column: "ReaderId");

            migrationBuilder.CreateIndex(
                name: "IX_PasswordResets_TokenHash",
                table: "PasswordResets",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PodcastEpisodes_PodcastId_Guid",
                table: "PodcastEpisodes",
                columns: new[] { "PodcastId", "Guid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Podcasts_OwnerId_FeedUrl",
                table: "Podcasts",
                columns: new[] { "OwnerId", "FeedUrl" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_Endpoint",
                table: "PushSubscriptions",
                column: "Endpoint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_ReaderId",
                table: "PushSubscriptions",
                column: "ReaderId");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_BookId",
                table: "Quotes",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_ReaderPasskeys_CredentialId",
                table: "ReaderPasskeys",
                column: "CredentialId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReaderPasskeys_ReaderId",
                table: "ReaderPasskeys",
                column: "ReaderId");

            migrationBuilder.CreateIndex(
                name: "IX_Readers_GuardianId",
                table: "Readers",
                column: "GuardianId");

            migrationBuilder.CreateIndex(
                name: "IX_Readers_KeyHash",
                table: "Readers",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Readers_KoboTokenHash",
                table: "Readers",
                column: "KoboTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Readers_NormalizedName",
                table: "Readers",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Readers_OidcSubject",
                table: "Readers",
                column: "OidcSubject",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Readers_PublicToken",
                table: "Readers",
                column: "PublicToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReaderSessions_ReaderId",
                table: "ReaderSessions",
                column: "ReaderId");

            migrationBuilder.CreateIndex(
                name: "IX_ReadingDays_BookId",
                table: "ReadingDays",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_ReadingDays_ReaderId_BookId_Day",
                table: "ReadingDays",
                columns: new[] { "ReaderId", "BookId", "Day" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecoveryCodes_ReaderId",
                table: "RecoveryCodes",
                column: "ReaderId");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteAsks_BookId_RemoteReaderId",
                table: "RemoteAsks",
                columns: new[] { "BookId", "RemoteReaderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RemoteAsks_RemoteReaderId",
                table: "RemoteAsks",
                column: "RemoteReaderId");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteReaders_LinkId_RemoteId",
                table: "RemoteReaders",
                columns: new[] { "LinkId", "RemoteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SavedSearches_OwnerId",
                table: "SavedSearches",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedSearches_ShareToken",
                table: "SavedSearches",
                column: "ShareToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SeriesAlerts_OwnerId_WorkKey",
                table: "SeriesAlerts",
                columns: new[] { "OwnerId", "WorkKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_BookId",
                table: "Sessions",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_ShelfLinks_OurSecretHash",
                table: "ShelfLinks",
                column: "OurSecretHash");

            migrationBuilder.CreateIndex(
                name: "IX_SpokenTexts_BookId",
                table: "SpokenTexts",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_Tags_Name",
                table: "Tags",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Webhooks_ReaderId",
                table: "Webhooks",
                column: "ReaderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiTokens");

            migrationBuilder.DropTable(
                name: "AudioBookmarks");

            migrationBuilder.DropTable(
                name: "AuditEntries");

            migrationBuilder.DropTable(
                name: "BookTags");

            migrationBuilder.DropTable(
                name: "BookTexts");

            migrationBuilder.DropTable(
                name: "ClubMembers");

            migrationBuilder.DropTable(
                name: "ClubPosts");

            migrationBuilder.DropTable(
                name: "CollectionBooks");

            migrationBuilder.DropTable(
                name: "Highlights");

            migrationBuilder.DropTable(
                name: "KoboEntries");

            migrationBuilder.DropTable(
                name: "KosyncPlaces");

            migrationBuilder.DropTable(
                name: "LinkedAsks");

            migrationBuilder.DropTable(
                name: "ListeningDays");

            migrationBuilder.DropTable(
                name: "LoanAsks");

            migrationBuilder.DropTable(
                name: "LoanPlaces");

            migrationBuilder.DropTable(
                name: "OcrPages");

            migrationBuilder.DropTable(
                name: "PartSummaries");

            migrationBuilder.DropTable(
                name: "PasswordResets");

            migrationBuilder.DropTable(
                name: "PodcastEpisodes");

            migrationBuilder.DropTable(
                name: "PushSubscriptions");

            migrationBuilder.DropTable(
                name: "Quotes");

            migrationBuilder.DropTable(
                name: "ReadAlongs");

            migrationBuilder.DropTable(
                name: "ReaderPasskeys");

            migrationBuilder.DropTable(
                name: "ReaderSessions");

            migrationBuilder.DropTable(
                name: "ReadingDays");

            migrationBuilder.DropTable(
                name: "RecoveryCodes");

            migrationBuilder.DropTable(
                name: "RemoteAsks");

            migrationBuilder.DropTable(
                name: "SavedSearches");

            migrationBuilder.DropTable(
                name: "SeriesAlerts");

            migrationBuilder.DropTable(
                name: "Sessions");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "SpokenTexts");

            migrationBuilder.DropTable(
                name: "Webhooks");

            migrationBuilder.DropTable(
                name: "Tags");

            migrationBuilder.DropTable(
                name: "ClubBooks");

            migrationBuilder.DropTable(
                name: "Collections");

            migrationBuilder.DropTable(
                name: "OcrScans");

            migrationBuilder.DropTable(
                name: "Podcasts");

            migrationBuilder.DropTable(
                name: "Clubs");

            migrationBuilder.DropTable(
                name: "Books");

            migrationBuilder.DropTable(
                name: "Readers");

            migrationBuilder.DropTable(
                name: "RemoteReaders");

            migrationBuilder.DropTable(
                name: "ShelfLinks");
        }
    }
}

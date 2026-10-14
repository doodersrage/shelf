using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Keeping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DismissedRelease",
                table: "Readers",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "HeldUntil",
                table: "Books",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HoldNote",
                table: "Books",
                type: "TEXT",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HomeLocation",
                table: "Books",
                type: "TEXT",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeftHow",
                table: "Books",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "LeftOn",
                table: "Books",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeftTo",
                table: "Books",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PricePaid",
                table: "Books",
                type: "TEXT",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QueueOrder",
                table: "Books",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SoldFor",
                table: "Books",
                type: "TEXT",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AutomationRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Conditions = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Actions = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomationRules_Readers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LegacyPlans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Instructions = table.Column<string>(type: "TEXT", nullable: true),
                    Keeper = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegacyPlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegacyPlans_Readers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LegacyRecipients",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Contact = table.Column<string>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    Tag = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    CollectionId = table.Column<int>(type: "INTEGER", nullable: true),
                    Rest = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegacyRecipients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegacyRecipients_Collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "Collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_LegacyRecipients_Readers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PositionChoices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ReaderId = table.Column<int>(type: "INTEGER", nullable: false),
                    BookId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Device = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Place = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Before = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PositionChoices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PositionChoices_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PositionChoices_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PositionReports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ReaderId = table.Column<int>(type: "INTEGER", nullable: false),
                    BookId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Device = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    EbookChapter = table.Column<int>(type: "INTEGER", nullable: true),
                    EbookCfi = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    EbookProgress = table.Column<double>(type: "REAL", nullable: true),
                    AudioTrack = table.Column<int>(type: "INTEGER", nullable: true),
                    AudioSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Kept = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PositionReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PositionReports_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PositionReports_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StoragePlaces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    ParentId = table.Column<int>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Temporary = table.Column<bool>(type: "INTEGER", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: true),
                    DestinationId = table.Column<int>(type: "INTEGER", nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    PackedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UnpackedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoragePlaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StoragePlaces_Readers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StoragePlaces_StoragePlaces_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "StoragePlaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StoragePlaces_StoragePlaces_ParentId",
                        column: x => x.ParentId,
                        principalTable: "StoragePlaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AutomationRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RuleId = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    BookId = table.Column<int>(type: "INTEGER", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DryRun = table.Column<bool>(type: "INTEGER", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    BatchId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomationRuns_AutomationRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "AutomationRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AutomationRuns_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRules_OwnerId_Trigger",
                table: "AutomationRules",
                columns: new[] { "OwnerId", "Trigger" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuns_BookId",
                table: "AutomationRuns",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuns_OwnerId_RuleId",
                table: "AutomationRuns",
                columns: new[] { "OwnerId", "RuleId" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuns_RuleId",
                table: "AutomationRuns",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_LegacyPlans_OwnerId",
                table: "LegacyPlans",
                column: "OwnerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegacyRecipients_CollectionId",
                table: "LegacyRecipients",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_LegacyRecipients_OwnerId",
                table: "LegacyRecipients",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_PositionChoices_BookId",
                table: "PositionChoices",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_PositionChoices_ReaderId_BookId",
                table: "PositionChoices",
                columns: new[] { "ReaderId", "BookId" });

            migrationBuilder.CreateIndex(
                name: "IX_PositionReports_BookId",
                table: "PositionReports",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_PositionReports_ReaderId_BookId_Source_Device",
                table: "PositionReports",
                columns: new[] { "ReaderId", "BookId", "Source", "Device" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StoragePlaces_DestinationId",
                table: "StoragePlaces",
                column: "DestinationId");

            migrationBuilder.CreateIndex(
                name: "IX_StoragePlaces_OwnerId",
                table: "StoragePlaces",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_StoragePlaces_ParentId",
                table: "StoragePlaces",
                column: "ParentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutomationRuns");

            migrationBuilder.DropTable(
                name: "LegacyPlans");

            migrationBuilder.DropTable(
                name: "LegacyRecipients");

            migrationBuilder.DropTable(
                name: "PositionChoices");

            migrationBuilder.DropTable(
                name: "PositionReports");

            migrationBuilder.DropTable(
                name: "StoragePlaces");

            migrationBuilder.DropTable(
                name: "AutomationRules");

            migrationBuilder.DropColumn(
                name: "DismissedRelease",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "HeldUntil",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "HoldNote",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "HomeLocation",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "LeftHow",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "LeftOn",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "LeftTo",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "PricePaid",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "QueueOrder",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "SoldFor",
                table: "Books");
        }
    }
}

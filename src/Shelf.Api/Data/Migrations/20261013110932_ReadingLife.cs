using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReadingLife : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "ReadBy",
                table: "Collections",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "CollectionBooks",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Spoiler",
                table: "ClubPosts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PreviousOwners",
                table: "Books",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Signed",
                table: "Books",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "BookLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FromBookId = table.Column<int>(type: "INTEGER", nullable: false),
                    ToBookId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookLinks_Books_FromBookId",
                        column: x => x.FromBookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BookLinks_Books_ToBookId",
                        column: x => x.ToBookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClubMeetings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClubId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClubBookId = table.Column<int>(type: "INTEGER", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Through = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Agenda = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClubMeetings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClubMeetings_ClubBooks_ClubBookId",
                        column: x => x.ClubBookId,
                        principalTable: "ClubBooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClubMeetings_Clubs_ClubId",
                        column: x => x.ClubId,
                        principalTable: "Clubs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CopyPhotos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BookId = table.Column<int>(type: "INTEGER", nullable: false),
                    StoredName = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    Caption = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AddedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CopyPhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CopyPhotos_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JournalEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    BookId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JournalEntries_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_JournalEntries_Readers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PastReads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BookId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    FinishedOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Rating = table.Column<int>(type: "INTEGER", nullable: true),
                    Thoughts = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PastReads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PastReads_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClubQuestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MeetingId = table.Column<int>(type: "INTEGER", nullable: false),
                    ReaderId = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    AskedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClubQuestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClubQuestions_ClubMeetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "ClubMeetings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClubQuestions_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookLinks_FromBookId",
                table: "BookLinks",
                column: "FromBookId");

            migrationBuilder.CreateIndex(
                name: "IX_BookLinks_ToBookId",
                table: "BookLinks",
                column: "ToBookId");

            migrationBuilder.CreateIndex(
                name: "IX_ClubMeetings_ClubBookId",
                table: "ClubMeetings",
                column: "ClubBookId");

            migrationBuilder.CreateIndex(
                name: "IX_ClubMeetings_ClubId",
                table: "ClubMeetings",
                column: "ClubId");

            migrationBuilder.CreateIndex(
                name: "IX_ClubQuestions_MeetingId",
                table: "ClubQuestions",
                column: "MeetingId");

            migrationBuilder.CreateIndex(
                name: "IX_ClubQuestions_ReaderId",
                table: "ClubQuestions",
                column: "ReaderId");

            migrationBuilder.CreateIndex(
                name: "IX_CopyPhotos_BookId",
                table: "CopyPhotos",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_BookId",
                table: "JournalEntries",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_OwnerId_Date",
                table: "JournalEntries",
                columns: new[] { "OwnerId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_PastReads_BookId",
                table: "PastReads",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_PastReads_FinishedOn",
                table: "PastReads",
                column: "FinishedOn");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookLinks");

            migrationBuilder.DropTable(
                name: "ClubQuestions");

            migrationBuilder.DropTable(
                name: "CopyPhotos");

            migrationBuilder.DropTable(
                name: "JournalEntries");

            migrationBuilder.DropTable(
                name: "PastReads");

            migrationBuilder.DropTable(
                name: "ClubMeetings");

            migrationBuilder.DropColumn(
                name: "ReadBy",
                table: "Collections");

            migrationBuilder.DropColumn(
                name: "Note",
                table: "CollectionBooks");

            migrationBuilder.DropColumn(
                name: "Spoiler",
                table: "ClubPosts");

            migrationBuilder.DropColumn(
                name: "PreviousOwners",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "Signed",
                table: "Books");
        }
    }
}

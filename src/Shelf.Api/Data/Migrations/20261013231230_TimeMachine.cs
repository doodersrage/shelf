using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TimeMachine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DueSoonDays",
                table: "Readers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.CreateTable(
                name: "ChangeBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UndoneAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangeBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChangeBatches_Readers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DetailProposals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    BookId = table.Column<int>(type: "INTEGER", nullable: false),
                    Field = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Current = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    OpenLibrary = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Google = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    FoundAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DetailProposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DetailProposals_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DetailProposals_Readers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FileRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    StoredName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CheckedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Problem = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    ProblemSince = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoanRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    BookId = table.Column<int>(type: "INTEGER", nullable: false),
                    BorrowerId = table.Column<int>(type: "INTEGER", nullable: true),
                    Borrower = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    LentOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    DueOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    ReturnedOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Renewals = table.Column<int>(type: "INTEGER", nullable: false),
                    ConditionOut = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    ConditionBack = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoanRecords_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LoanRecords_Readers_BorrowerId",
                        column: x => x.BorrowerId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "NotDuplicates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    BookId = table.Column<int>(type: "INTEGER", nullable: false),
                    OtherId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotDuplicates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotDuplicates_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NotDuplicates_Books_OtherId",
                        column: x => x.OtherId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NotDuplicates_Readers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScanSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Location = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Codes = table.Column<string>(type: "TEXT", nullable: false),
                    Seen = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScanSessions_Readers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BookChanges",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    BookId = table.Column<int>(type: "INTEGER", nullable: true),
                    BatchId = table.Column<int>(type: "INTEGER", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Field = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Before = table.Column<string>(type: "TEXT", nullable: true),
                    After = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookChanges_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BookChanges_ChangeBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "ChangeBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BookChanges_Readers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TrashItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    BookId = table.Column<int>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    BatchId = table.Column<int>(type: "INTEGER", nullable: true),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    Files = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrashItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrashItems_ChangeBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "ChangeBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TrashItems_Readers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookChanges_At",
                table: "BookChanges",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_BookChanges_BatchId",
                table: "BookChanges",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_BookChanges_BookId_Id",
                table: "BookChanges",
                columns: new[] { "BookId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_BookChanges_OwnerId",
                table: "BookChanges",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_ChangeBatches_OwnerId_Id",
                table: "ChangeBatches",
                columns: new[] { "OwnerId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DetailProposals_BookId",
                table: "DetailProposals",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_DetailProposals_OwnerId_BookId",
                table: "DetailProposals",
                columns: new[] { "OwnerId", "BookId" });

            migrationBuilder.CreateIndex(
                name: "IX_FileRecords_Kind_StoredName",
                table: "FileRecords",
                columns: new[] { "Kind", "StoredName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FileRecords_Problem",
                table: "FileRecords",
                column: "Problem");

            migrationBuilder.CreateIndex(
                name: "IX_LoanRecords_BookId_ReturnedOn",
                table: "LoanRecords",
                columns: new[] { "BookId", "ReturnedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_LoanRecords_BorrowerId",
                table: "LoanRecords",
                column: "BorrowerId");

            migrationBuilder.CreateIndex(
                name: "IX_LoanRecords_OwnerId_ReturnedOn",
                table: "LoanRecords",
                columns: new[] { "OwnerId", "ReturnedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_NotDuplicates_BookId",
                table: "NotDuplicates",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_NotDuplicates_OtherId",
                table: "NotDuplicates",
                column: "OtherId");

            migrationBuilder.CreateIndex(
                name: "IX_NotDuplicates_OwnerId_BookId_OtherId",
                table: "NotDuplicates",
                columns: new[] { "OwnerId", "BookId", "OtherId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScanSessions_OwnerId",
                table: "ScanSessions",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_TrashItems_BatchId",
                table: "TrashItems",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_TrashItems_OwnerId_DeletedAt",
                table: "TrashItems",
                columns: new[] { "OwnerId", "DeletedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookChanges");

            migrationBuilder.DropTable(
                name: "DetailProposals");

            migrationBuilder.DropTable(
                name: "FileRecords");

            migrationBuilder.DropTable(
                name: "LoanRecords");

            migrationBuilder.DropTable(
                name: "NotDuplicates");

            migrationBuilder.DropTable(
                name: "ScanSessions");

            migrationBuilder.DropTable(
                name: "TrashItems");

            migrationBuilder.DropTable(
                name: "ChangeBatches");

            migrationBuilder.DropColumn(
                name: "DueSoonDays",
                table: "Readers");
        }
    }
}

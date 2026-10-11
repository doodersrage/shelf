using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AbsSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AbsLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ReaderId = table.Column<int>(type: "INTEGER", nullable: false),
                    Server = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ItemId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    BookId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AbsLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AbsLinks_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AbsLinks_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AbsSyncs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ReaderId = table.Column<int>(type: "INTEGER", nullable: false),
                    Server = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    LibraryId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    LibraryName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SealedKey = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    BringProgress = table.Column<bool>(type: "INTEGER", nullable: false),
                    CheckedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastFound = table.Column<int>(type: "INTEGER", nullable: true),
                    Problem = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CheckSoon = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AbsSyncs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AbsSyncs_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AbsLinks_BookId",
                table: "AbsLinks",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_AbsLinks_ReaderId_Server_ItemId",
                table: "AbsLinks",
                columns: new[] { "ReaderId", "Server", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AbsSyncs_ReaderId",
                table: "AbsSyncs",
                column: "ReaderId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AbsLinks");

            migrationBuilder.DropTable(
                name: "AbsSyncs");
        }
    }
}

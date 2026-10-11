using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Kobo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "KoboCollectionId",
                table: "Readers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KoboTokenHash",
                table: "Readers",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "KoboEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ReaderId = table.Column<int>(type: "INTEGER", nullable: false),
                    BookId = table.Column<int>(type: "INTEGER", nullable: false),
                    Archived = table.Column<bool>(type: "INTEGER", nullable: false),
                    Bookmark = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    SyncedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
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

            migrationBuilder.CreateIndex(
                name: "IX_Readers_KoboTokenHash",
                table: "Readers",
                column: "KoboTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KoboEntries_ReaderId_BookId",
                table: "KoboEntries",
                columns: new[] { "ReaderId", "BookId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KoboEntries");

            migrationBuilder.DropIndex(
                name: "IX_Readers_KoboTokenHash",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "KoboCollectionId",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "KoboTokenHash",
                table: "Readers");
        }
    }
}

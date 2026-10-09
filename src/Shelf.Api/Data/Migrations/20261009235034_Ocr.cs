using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Ocr : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OcrScans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BookId = table.Column<int>(type: "INTEGER", nullable: false),
                    StoredName = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    Pages = table.Column<int>(type: "INTEGER", nullable: false),
                    Done = table.Column<int>(type: "INTEGER", nullable: false),
                    Failed = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
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
                name: "OcrPages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScanId = table.Column<int>(type: "INTEGER", nullable: false),
                    Page = table.Column<int>(type: "INTEGER", nullable: false),
                    Words = table.Column<string>(type: "TEXT", nullable: false)
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
                name: "IX_OcrPages_ScanId_Page",
                table: "OcrPages",
                columns: new[] { "ScanId", "Page" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OcrScans_BookId_StoredName",
                table: "OcrScans",
                columns: new[] { "BookId", "StoredName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OcrPages");

            migrationBuilder.DropTable(
                name: "OcrScans");
        }
    }
}

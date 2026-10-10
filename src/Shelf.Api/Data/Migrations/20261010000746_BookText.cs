using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BookText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Readings made before search kept no text, so every e-book is read once more.
            migrationBuilder.Sql("DELETE FROM \"OcrPages\";");
            migrationBuilder.Sql("DELETE FROM \"OcrScans\";");

            migrationBuilder.CreateTable(
                name: "BookTexts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScanId = table.Column<int>(type: "INTEGER", nullable: false),
                    BookId = table.Column<int>(type: "INTEGER", nullable: false),
                    Part = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false)
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

            migrationBuilder.CreateIndex(
                name: "IX_BookTexts_BookId_Part",
                table: "BookTexts",
                columns: new[] { "BookId", "Part" });

            migrationBuilder.CreateIndex(
                name: "IX_BookTexts_ScanId",
                table: "BookTexts",
                column: "ScanId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookTexts");
        }
    }
}

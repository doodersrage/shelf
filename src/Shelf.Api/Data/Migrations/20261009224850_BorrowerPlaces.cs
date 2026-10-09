using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BorrowerPlaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReaderId",
                table: "Highlights",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LoanPlaces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BookId = table.Column<int>(type: "INTEGER", nullable: false),
                    ReaderId = table.Column<int>(type: "INTEGER", nullable: false),
                    EbookChapter = table.Column<int>(type: "INTEGER", nullable: true),
                    AudioTrack = table.Column<int>(type: "INTEGER", nullable: true),
                    AudioSeconds = table.Column<int>(type: "INTEGER", nullable: true)
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

            migrationBuilder.CreateIndex(
                name: "IX_Highlights_ReaderId",
                table: "Highlights",
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

            migrationBuilder.AddForeignKey(
                name: "FK_Highlights_Readers_ReaderId",
                table: "Highlights",
                column: "ReaderId",
                principalTable: "Readers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Highlights_Readers_ReaderId",
                table: "Highlights");

            migrationBuilder.DropTable(
                name: "LoanPlaces");

            migrationBuilder.DropIndex(
                name: "IX_Highlights_ReaderId",
                table: "Highlights");

            migrationBuilder.DropColumn(
                name: "ReaderId",
                table: "Highlights");
        }
    }
}

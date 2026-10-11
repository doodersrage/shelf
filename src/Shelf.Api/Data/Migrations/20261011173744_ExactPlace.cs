using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExactPlace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EbookCfi",
                table: "LoanPlaces",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "EbookProgress",
                table: "LoanPlaces",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EbookCfi",
                table: "Books",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "EbookProgress",
                table: "Books",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EbookCfi",
                table: "LoanPlaces");

            migrationBuilder.DropColumn(
                name: "EbookProgress",
                table: "LoanPlaces");

            migrationBuilder.DropColumn(
                name: "EbookCfi",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "EbookProgress",
                table: "Books");
        }
    }
}

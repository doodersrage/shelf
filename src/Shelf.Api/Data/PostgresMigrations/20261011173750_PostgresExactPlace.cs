using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.PostgresMigrations
{
    /// <inheritdoc />
    public partial class PostgresExactPlace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EbookCfi",
                table: "LoanPlaces",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "EbookProgress",
                table: "LoanPlaces",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EbookCfi",
                table: "Books",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "EbookProgress",
                table: "Books",
                type: "double precision",
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

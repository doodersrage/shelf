using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Hardcover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HardcoverAuto",
                table: "Readers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "HardcoverToken",
                table: "Readers",
                type: "TEXT",
                maxLength: 6000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HardcoverBookId",
                table: "Books",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HardcoverState",
                table: "Books",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HardcoverUserBookId",
                table: "Books",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HardcoverAuto",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "HardcoverToken",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "HardcoverBookId",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "HardcoverState",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "HardcoverUserBookId",
                table: "Books");
        }
    }
}

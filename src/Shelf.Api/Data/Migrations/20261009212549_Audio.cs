using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Audio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AudioFileName",
                table: "Books",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AudioSeconds",
                table: "Books",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AudioStoredName",
                table: "Books",
                type: "TEXT",
                maxLength: 48,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AudioTrack",
                table: "Books",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AudioFileName",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "AudioSeconds",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "AudioStoredName",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "AudioTrack",
                table: "Books");
        }
    }
}

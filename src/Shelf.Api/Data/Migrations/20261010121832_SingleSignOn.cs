using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SingleSignOn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OidcSubject",
                table: "Readers",
                type: "TEXT",
                maxLength: 600,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PasswordUnknown",
                table: "Readers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Readers_OidcSubject",
                table: "Readers",
                column: "OidcSubject",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Readers_OidcSubject",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "OidcSubject",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "PasswordUnknown",
                table: "Readers");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Narrator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Narrator",
                table: "Books",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            // The Audiobookshelf import used to put the narrator in the notes, as "Read by …." in the reader's
            // language; it moves across.
            foreach (var prefix in new[] { "Read by ", "Narrado por ", "Lu par ", "Gelesen von " })
            {
                migrationBuilder.Sql($"""
                    UPDATE Books
                    SET Narrator = substr(Notes, {prefix.Length + 1}, length(Notes) - {prefix.Length + 1}), Notes = NULL
                    WHERE Narrator IS NULL AND Notes LIKE '{prefix}%.' AND length(Notes) BETWEEN {prefix.Length + 2} AND {prefix.Length + 201} AND instr(Notes, char(10)) = 0
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Narrator",
                table: "Books");
        }
    }
}

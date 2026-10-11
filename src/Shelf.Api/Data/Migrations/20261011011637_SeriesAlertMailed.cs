using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeriesAlertMailed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Mailed",
                table: "SeriesAlerts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Books found before this release are not news by email.
            migrationBuilder.Sql("UPDATE SeriesAlerts SET Mailed = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Mailed",
                table: "SeriesAlerts");
        }
    }
}

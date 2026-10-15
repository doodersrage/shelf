using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.PostgresMigrations
{
    /// <inheritdoc />
    public partial class PostgresMoreNotices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HealthTold",
                table: "Readers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KnownDevices",
                table: "Readers",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DayReminded",
                table: "ClubMeetings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HourReminded",
                table: "ClubMeetings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // New readers hear of new readers and Server health when they are admins; so do readers from before.
            migrationBuilder.Sql("UPDATE \"Readers\" SET \"NoticeKinds\" = \"NoticeKinds\" | 16;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HealthTold",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "KnownDevices",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "DayReminded",
                table: "ClubMeetings");

            migrationBuilder.DropColumn(
                name: "HourReminded",
                table: "ClubMeetings");
        }
    }
}

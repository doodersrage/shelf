using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class GoalsZonesAndEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TimeZone",
                table: "ClubMeetings",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Areas",
                table: "ApiTokens",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EventRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ReaderId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Payload = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventRecords_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReadingGoals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Target = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReadingGoals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReadingGoals_Readers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventRecords_ReaderId_Id",
                table: "EventRecords",
                columns: new[] { "ReaderId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ReadingGoals_OwnerId_Year",
                table: "ReadingGoals",
                columns: new[] { "OwnerId", "Year" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventRecords");

            migrationBuilder.DropTable(
                name: "ReadingGoals");

            migrationBuilder.DropColumn(
                name: "TimeZone",
                table: "ClubMeetings");

            migrationBuilder.DropColumn(
                name: "Areas",
                table: "ApiTokens");
        }
    }
}

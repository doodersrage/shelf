using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Shelf.Api.Data.PostgresMigrations
{
    /// <inheritdoc />
    public partial class PostgresGoalsZonesAndEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TimeZone",
                table: "ClubMeetings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Areas",
                table: "ApiTokens",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EventRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReaderId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Payload = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false)
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerId = table.Column<int>(type: "integer", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Target = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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

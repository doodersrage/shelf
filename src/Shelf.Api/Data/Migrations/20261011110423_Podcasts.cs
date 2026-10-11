using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Podcasts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Podcasts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    FeedUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Author = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    ImageUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    KeepLatest = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CheckedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Problem = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Podcasts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Podcasts_Readers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PodcastEpisodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PodcastId = table.Column<int>(type: "INTEGER", nullable: false),
                    Guid = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AudioUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    DurationSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    StoredName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Seconds = table.Column<double>(type: "REAL", nullable: false),
                    Played = table.Column<bool>(type: "INTEGER", nullable: false),
                    PlayedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FoundAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PodcastEpisodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PodcastEpisodes_Podcasts_PodcastId",
                        column: x => x.PodcastId,
                        principalTable: "Podcasts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PodcastEpisodes_PodcastId_Guid",
                table: "PodcastEpisodes",
                columns: new[] { "PodcastId", "Guid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Podcasts_OwnerId_FeedUrl",
                table: "Podcasts",
                columns: new[] { "OwnerId", "FeedUrl" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PodcastEpisodes");

            migrationBuilder.DropTable(
                name: "Podcasts");
        }
    }
}

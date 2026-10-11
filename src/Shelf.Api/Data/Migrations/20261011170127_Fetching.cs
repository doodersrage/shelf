using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Fetching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FetchGrabs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ReaderId = table.Column<int>(type: "INTEGER", nullable: false),
                    BookId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Indexer = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DownloadId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Progress = table.Column<double>(type: "REAL", nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Problem = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FetchGrabs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FetchGrabs_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FetchGrabs_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FetchSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IndexerKind = table.Column<int>(type: "INTEGER", nullable: false),
                    IndexerUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    SealedIndexerKey = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    TorrentKind = table.Column<int>(type: "INTEGER", nullable: false),
                    TorrentUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    TorrentUser = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SealedTorrentPassword = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    SabUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    SealedSabKey = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Category = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    RemotePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    LocalPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    EbookFormats = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AudioFormats = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    MinSeeders = table.Column<int>(type: "INTEGER", nullable: false),
                    SearchWanted = table.Column<bool>(type: "INTEGER", nullable: false),
                    WantedKind = table.Column<int>(type: "INTEGER", nullable: false),
                    MostPerDay = table.Column<int>(type: "INTEGER", nullable: false),
                    WantedSearchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FetchSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FetchGrabs_BookId",
                table: "FetchGrabs",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_FetchGrabs_ReaderId_BookId",
                table: "FetchGrabs",
                columns: new[] { "ReaderId", "BookId" });

            migrationBuilder.CreateIndex(
                name: "IX_FetchGrabs_State",
                table: "FetchGrabs",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FetchGrabs");

            migrationBuilder.DropTable(
                name: "FetchSettings");
        }
    }
}

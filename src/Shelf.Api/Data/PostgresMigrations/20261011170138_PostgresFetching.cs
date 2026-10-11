using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Shelf.Api.Data.PostgresMigrations
{
    /// <inheritdoc />
    public partial class PostgresFetching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FetchGrabs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReaderId = table.Column<int>(type: "integer", nullable: false),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Indexer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Protocol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DownloadId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Progress = table.Column<double>(type: "double precision", nullable: false),
                    Path = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Problem = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    IndexerKind = table.Column<int>(type: "integer", nullable: false),
                    IndexerUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SealedIndexerKey = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    TorrentKind = table.Column<int>(type: "integer", nullable: false),
                    TorrentUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TorrentUser = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SealedTorrentPassword = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    SabUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SealedSabKey = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RemotePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LocalPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    EbookFormats = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AudioFormats = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MinSeeders = table.Column<int>(type: "integer", nullable: false),
                    SearchWanted = table.Column<bool>(type: "boolean", nullable: false),
                    WantedKind = table.Column<int>(type: "integer", nullable: false),
                    MostPerDay = table.Column<int>(type: "integer", nullable: false),
                    WantedSearchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
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

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Shelf.Api.Data.PostgresMigrations
{
    /// <inheritdoc />
    public partial class PostgresAbsSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AbsLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReaderId = table.Column<int>(type: "integer", nullable: false),
                    Server = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ItemId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    BookId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AbsLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AbsLinks_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AbsLinks_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AbsSyncs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReaderId = table.Column<int>(type: "integer", nullable: false),
                    Server = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    LibraryId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LibraryName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SealedKey = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    BringProgress = table.Column<bool>(type: "boolean", nullable: false),
                    CheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastFound = table.Column<int>(type: "integer", nullable: true),
                    Problem = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CheckSoon = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AbsSyncs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AbsSyncs_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AbsLinks_BookId",
                table: "AbsLinks",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_AbsLinks_ReaderId_Server_ItemId",
                table: "AbsLinks",
                columns: new[] { "ReaderId", "Server", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AbsSyncs_ReaderId",
                table: "AbsSyncs",
                column: "ReaderId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AbsLinks");

            migrationBuilder.DropTable(
                name: "AbsSyncs");
        }
    }
}

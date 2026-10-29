using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Shelf.Api.Data.PostgresMigrations
{
    /// <inheritdoc />
    public partial class PostgresListeningSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ListeningSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReaderId = table.Column<int>(type: "integer", nullable: false),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    Device = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Seconds = table.Column<int>(type: "integer", nullable: false),
                    FromSeconds = table.Column<double>(type: "double precision", nullable: true),
                    ToSeconds = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListeningSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ListeningSessions_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ListeningSessions_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ListeningSessions_BookId",
                table: "ListeningSessions",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_ListeningSessions_ReaderId_BookId",
                table: "ListeningSessions",
                columns: new[] { "ReaderId", "BookId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ListeningSessions");
        }
    }
}

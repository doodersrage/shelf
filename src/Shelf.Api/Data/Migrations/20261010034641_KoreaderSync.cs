using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class KoreaderSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "KosyncHash",
                table: "Readers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KoreaderDigest",
                table: "Books",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "KosyncPlaces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ReaderId = table.Column<int>(type: "INTEGER", nullable: false),
                    BookId = table.Column<int>(type: "INTEGER", nullable: false),
                    Progress = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Percentage = table.Column<double>(type: "REAL", nullable: false),
                    Chapter = table.Column<int>(type: "INTEGER", nullable: false),
                    Device = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KosyncPlaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KosyncPlaces_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KosyncPlaces_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KosyncPlaces_BookId",
                table: "KosyncPlaces",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_KosyncPlaces_ReaderId_BookId",
                table: "KosyncPlaces",
                columns: new[] { "ReaderId", "BookId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KosyncPlaces");

            migrationBuilder.DropColumn(
                name: "KosyncHash",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "KoreaderDigest",
                table: "Books");
        }
    }
}

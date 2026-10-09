using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AdminsAndAsks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAdmin",
                table: "Readers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ShelfOpen",
                table: "Readers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Stamp",
                table: "Readers",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            // Readers from before stamps get one each, and the first of them looks after the shelf.
            migrationBuilder.Sql("UPDATE \"Readers\" SET \"Stamp\" = lower(hex(randomblob(16)));");
            migrationBuilder.Sql("UPDATE \"Readers\" SET \"IsAdmin\" = 1 WHERE \"Id\" = (SELECT MIN(\"Id\") FROM \"Readers\");");

            migrationBuilder.CreateTable(
                name: "LoanAsks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BookId = table.Column<int>(type: "INTEGER", nullable: false),
                    ReaderId = table.Column<int>(type: "INTEGER", nullable: false),
                    AskedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanAsks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoanAsks_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LoanAsks_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LoanAsks_BookId_ReaderId",
                table: "LoanAsks",
                columns: new[] { "BookId", "ReaderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoanAsks_ReaderId",
                table: "LoanAsks",
                column: "ReaderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoanAsks");

            migrationBuilder.DropColumn(
                name: "IsAdmin",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "ShelfOpen",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "Stamp",
                table: "Readers");
        }
    }
}

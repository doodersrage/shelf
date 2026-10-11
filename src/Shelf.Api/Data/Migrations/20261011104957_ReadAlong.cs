using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReadAlong : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReadAlongs",
                columns: table => new
                {
                    BookId = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Done = table.Column<int>(type: "INTEGER", nullable: false),
                    Total = table.Column<int>(type: "INTEGER", nullable: false),
                    Problem = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    AudioStoredName = table.Column<string>(type: "TEXT", maxLength: 48, nullable: true),
                    EbookStoredName = table.Column<string>(type: "TEXT", maxLength: 48, nullable: true),
                    Transcript = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Timings = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Matched = table.Column<double>(type: "REAL", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReadAlongs", x => x.BookId);
                    table.ForeignKey(
                        name: "FK_ReadAlongs_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReadAlongs");
        }
    }
}

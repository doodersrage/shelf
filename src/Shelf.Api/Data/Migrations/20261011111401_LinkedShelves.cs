using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class LinkedShelves : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RemoteBorrowerId",
                table: "Books",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VisitingBookId",
                table: "Books",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "VisitingDueOn",
                table: "Books",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VisitingLinkId",
                table: "Books",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VisitingOwner",
                table: "Books",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ShelfLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Address = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    TheirSecret = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    OurSecretHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    InviteHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    InviteExpires = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShelfLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LinkedAsks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LinkId = table.Column<int>(type: "INTEGER", nullable: false),
                    ReaderId = table.Column<int>(type: "INTEGER", nullable: false),
                    RemoteBookId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Author = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Owner = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                    AskedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkedAsks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LinkedAsks_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LinkedAsks_ShelfLinks_LinkId",
                        column: x => x.LinkId,
                        principalTable: "ShelfLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RemoteReaders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LinkId = table.Column<int>(type: "INTEGER", nullable: false),
                    RemoteId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteReaders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RemoteReaders_ShelfLinks_LinkId",
                        column: x => x.LinkId,
                        principalTable: "ShelfLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RemoteAsks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BookId = table.Column<int>(type: "INTEGER", nullable: false),
                    RemoteReaderId = table.Column<int>(type: "INTEGER", nullable: false),
                    AskedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteAsks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RemoteAsks_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RemoteAsks_RemoteReaders_RemoteReaderId",
                        column: x => x.RemoteReaderId,
                        principalTable: "RemoteReaders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Books_RemoteBorrowerId",
                table: "Books",
                column: "RemoteBorrowerId");

            migrationBuilder.CreateIndex(
                name: "IX_Books_VisitingLinkId",
                table: "Books",
                column: "VisitingLinkId");

            migrationBuilder.CreateIndex(
                name: "IX_LinkedAsks_LinkId",
                table: "LinkedAsks",
                column: "LinkId");

            migrationBuilder.CreateIndex(
                name: "IX_LinkedAsks_ReaderId_LinkId_RemoteBookId",
                table: "LinkedAsks",
                columns: new[] { "ReaderId", "LinkId", "RemoteBookId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RemoteAsks_BookId_RemoteReaderId",
                table: "RemoteAsks",
                columns: new[] { "BookId", "RemoteReaderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RemoteAsks_RemoteReaderId",
                table: "RemoteAsks",
                column: "RemoteReaderId");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteReaders_LinkId_RemoteId",
                table: "RemoteReaders",
                columns: new[] { "LinkId", "RemoteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShelfLinks_OurSecretHash",
                table: "ShelfLinks",
                column: "OurSecretHash");

            migrationBuilder.AddForeignKey(
                name: "FK_Books_RemoteReaders_RemoteBorrowerId",
                table: "Books",
                column: "RemoteBorrowerId",
                principalTable: "RemoteReaders",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Books_ShelfLinks_VisitingLinkId",
                table: "Books",
                column: "VisitingLinkId",
                principalTable: "ShelfLinks",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Books_RemoteReaders_RemoteBorrowerId",
                table: "Books");

            migrationBuilder.DropForeignKey(
                name: "FK_Books_ShelfLinks_VisitingLinkId",
                table: "Books");

            migrationBuilder.DropTable(
                name: "LinkedAsks");

            migrationBuilder.DropTable(
                name: "RemoteAsks");

            migrationBuilder.DropTable(
                name: "RemoteReaders");

            migrationBuilder.DropTable(
                name: "ShelfLinks");

            migrationBuilder.DropIndex(
                name: "IX_Books_RemoteBorrowerId",
                table: "Books");

            migrationBuilder.DropIndex(
                name: "IX_Books_VisitingLinkId",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "RemoteBorrowerId",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "VisitingBookId",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "VisitingDueOn",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "VisitingLinkId",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "VisitingOwner",
                table: "Books");
        }
    }
}

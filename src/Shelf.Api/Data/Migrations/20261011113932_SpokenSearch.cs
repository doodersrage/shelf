using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SpokenSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SpokenTexts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BookId = table.Column<int>(type: "INTEGER", nullable: false),
                    AudioStoredName = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    Start = table.Column<double>(type: "REAL", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpokenTexts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpokenTexts_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpokenTexts_BookId",
                table: "SpokenTexts",
                column: "BookId");

            // A full-text index over what recordings say, kept in step by triggers, as BookTextSearch is for text.
            migrationBuilder.Sql("""
                CREATE VIRTUAL TABLE "SpokenTextSearch" USING fts5(
                    "Text", content='SpokenTexts', content_rowid='Id', tokenize='unicode61 remove_diacritics 2');
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER "SpokenTexts_Inserted" AFTER INSERT ON "SpokenTexts" BEGIN
                    INSERT INTO "SpokenTextSearch"(rowid, "Text") VALUES (new."Id", new."Text");
                END;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER "SpokenTexts_Deleted" AFTER DELETE ON "SpokenTexts" BEGIN
                    INSERT INTO "SpokenTextSearch"("SpokenTextSearch", rowid, "Text") VALUES ('delete', old."Id", old."Text");
                END;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER "SpokenTexts_Updated" AFTER UPDATE ON "SpokenTexts" BEGIN
                    INSERT INTO "SpokenTextSearch"("SpokenTextSearch", rowid, "Text") VALUES ('delete', old."Id", old."Text");
                    INSERT INTO "SpokenTextSearch"(rowid, "Text") VALUES (new."Id", new."Text");
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TRIGGER IF EXISTS "SpokenTexts_Updated";""");
            migrationBuilder.Sql("""DROP TRIGGER IF EXISTS "SpokenTexts_Deleted";""");
            migrationBuilder.Sql("""DROP TRIGGER IF EXISTS "SpokenTexts_Inserted";""");
            migrationBuilder.Sql("""DROP TABLE IF EXISTS "SpokenTextSearch";""");

            migrationBuilder.DropTable(
                name: "SpokenTexts");
        }
    }
}

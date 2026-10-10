using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BookTextSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A full-text index over BookTexts, kept in step by triggers: ranked, and blind to case and accents.
            migrationBuilder.Sql("""
                CREATE VIRTUAL TABLE "BookTextSearch" USING fts5(
                    "Text", content='BookTexts', content_rowid='Id', tokenize='unicode61 remove_diacritics 2');
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER "BookTexts_Inserted" AFTER INSERT ON "BookTexts" BEGIN
                    INSERT INTO "BookTextSearch"(rowid, "Text") VALUES (new."Id", new."Text");
                END;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER "BookTexts_Deleted" AFTER DELETE ON "BookTexts" BEGIN
                    INSERT INTO "BookTextSearch"("BookTextSearch", rowid, "Text") VALUES ('delete', old."Id", old."Text");
                END;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER "BookTexts_Updated" AFTER UPDATE ON "BookTexts" BEGIN
                    INSERT INTO "BookTextSearch"("BookTextSearch", rowid, "Text") VALUES ('delete', old."Id", old."Text");
                    INSERT INTO "BookTextSearch"(rowid, "Text") VALUES (new."Id", new."Text");
                END;
                """);
            migrationBuilder.Sql("""INSERT INTO "BookTextSearch"("BookTextSearch") VALUES ('rebuild');""");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TRIGGER IF EXISTS "BookTexts_Updated";""");
            migrationBuilder.Sql("""DROP TRIGGER IF EXISTS "BookTexts_Deleted";""");
            migrationBuilder.Sql("""DROP TRIGGER IF EXISTS "BookTexts_Inserted";""");
            migrationBuilder.Sql("""DROP TABLE IF EXISTS "BookTextSearch";""");

        }
    }
}

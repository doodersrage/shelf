using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ShelfLife : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SandboxOf",
                table: "Readers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ImportRecipes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Spec = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportRecipes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportRecipes_Readers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ParallelPassages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    BookId = table.Column<int>(type: "INTEGER", nullable: true),
                    OtherBookId = table.Column<int>(type: "INTEGER", nullable: true),
                    Original = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    OriginalLanguage = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Translation = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    TranslationLanguage = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Place = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParallelPassages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ParallelPassages_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ParallelPassages_Books_OtherBookId",
                        column: x => x.OtherBookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ParallelPassages_Readers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ResearchTopics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Question = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResearchTopics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResearchTopics_Readers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VocabularyWords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    BookId = table.Column<int>(type: "INTEGER", nullable: true),
                    Word = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Language = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Meaning = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Context = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    AddedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Box = table.Column<int>(type: "INTEGER", nullable: false),
                    DueOn = table.Column<DateOnly>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VocabularyWords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VocabularyWords_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_VocabularyWords_Readers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ResearchNotes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TopicId = table.Column<int>(type: "INTEGER", nullable: false),
                    BookId = table.Column<int>(type: "INTEGER", nullable: true),
                    Place = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Quote = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Text = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Link = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResearchNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResearchNotes_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ResearchNotes_ResearchTopics_TopicId",
                        column: x => x.TopicId,
                        principalTable: "ResearchTopics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Readers_SandboxOf",
                table: "Readers",
                column: "SandboxOf");

            migrationBuilder.CreateIndex(
                name: "IX_ImportRecipes_OwnerId_Name",
                table: "ImportRecipes",
                columns: new[] { "OwnerId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ParallelPassages_BookId",
                table: "ParallelPassages",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_ParallelPassages_OtherBookId",
                table: "ParallelPassages",
                column: "OtherBookId");

            migrationBuilder.CreateIndex(
                name: "IX_ParallelPassages_OwnerId",
                table: "ParallelPassages",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_ResearchNotes_BookId",
                table: "ResearchNotes",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_ResearchNotes_TopicId",
                table: "ResearchNotes",
                column: "TopicId");

            migrationBuilder.CreateIndex(
                name: "IX_ResearchTopics_OwnerId",
                table: "ResearchTopics",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_VocabularyWords_BookId",
                table: "VocabularyWords",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_VocabularyWords_OwnerId_DueOn",
                table: "VocabularyWords",
                columns: new[] { "OwnerId", "DueOn" });

            migrationBuilder.CreateIndex(
                name: "IX_VocabularyWords_OwnerId_Language_Word",
                table: "VocabularyWords",
                columns: new[] { "OwnerId", "Language", "Word" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Readers_Readers_SandboxOf",
                table: "Readers",
                column: "SandboxOf",
                principalTable: "Readers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Readers_Readers_SandboxOf",
                table: "Readers");

            migrationBuilder.DropTable(
                name: "ImportRecipes");

            migrationBuilder.DropTable(
                name: "ParallelPassages");

            migrationBuilder.DropTable(
                name: "ResearchNotes");

            migrationBuilder.DropTable(
                name: "VocabularyWords");

            migrationBuilder.DropTable(
                name: "ResearchTopics");

            migrationBuilder.DropIndex(
                name: "IX_Readers_SandboxOf",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "SandboxOf",
                table: "Readers");
        }
    }
}

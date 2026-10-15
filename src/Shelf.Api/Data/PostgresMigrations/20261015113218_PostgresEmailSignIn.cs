using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.PostgresMigrations
{
    /// <inheritdoc />
    public partial class PostgresEmailSignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Readers_NormalizedName",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "LastReminderOn",
                table: "Readers");

            migrationBuilder.RenameColumn(
                name: "EmailReminders",
                table: "Readers",
                newName: "EmailNotices");

            // Notifications by email are on for every reader, who can turn them off; they go to the address the reader
            // signs in with, which the address given before becomes when Shelf starts (ReaderRules.AdoptEmailsAsync).
            migrationBuilder.Sql("UPDATE \"Readers\" SET \"EmailNotices\" = TRUE;");

            migrationBuilder.AddColumn<string>(
                name: "NormalizedEmail",
                table: "Readers",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingEmail",
                table: "Readers",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PendingEmailExpires",
                table: "Readers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingEmailTokenHash",
                table: "Readers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingNormalizedEmail",
                table: "Readers",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Readers_NormalizedEmail",
                table: "Readers",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Readers_NormalizedName",
                table: "Readers",
                column: "NormalizedName");

            migrationBuilder.CreateIndex(
                name: "IX_Readers_PendingEmailTokenHash",
                table: "Readers",
                column: "PendingEmailTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Readers_PendingNormalizedEmail",
                table: "Readers",
                column: "PendingNormalizedEmail");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Readers_NormalizedEmail",
                table: "Readers");

            migrationBuilder.DropIndex(
                name: "IX_Readers_NormalizedName",
                table: "Readers");

            migrationBuilder.DropIndex(
                name: "IX_Readers_PendingEmailTokenHash",
                table: "Readers");

            migrationBuilder.DropIndex(
                name: "IX_Readers_PendingNormalizedEmail",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "NormalizedEmail",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "PendingEmail",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "PendingEmailExpires",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "PendingEmailTokenHash",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "PendingNormalizedEmail",
                table: "Readers");

            migrationBuilder.RenameColumn(
                name: "EmailNotices",
                table: "Readers",
                newName: "EmailReminders");

            migrationBuilder.AddColumn<DateOnly>(
                name: "LastReminderOn",
                table: "Readers",
                type: "date",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Readers_NormalizedName",
                table: "Readers",
                column: "NormalizedName",
                unique: true);
        }
    }
}

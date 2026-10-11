using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TwoWay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HardcoverAddMissing",
                table: "Readers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ReadwiseAutoImport",
                table: "Readers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReadwiseImportedAt",
                table: "Readers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HardcoverAddMissing",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "ReadwiseAutoImport",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "ReadwiseImportedAt",
                table: "Readers");
        }
    }
}

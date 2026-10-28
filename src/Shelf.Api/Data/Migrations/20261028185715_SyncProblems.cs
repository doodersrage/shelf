using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SyncProblems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HardcoverProblem",
                table: "Readers",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HardcoverProblemAt",
                table: "Readers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReadwiseProblem",
                table: "Readers",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReadwiseProblemAt",
                table: "Readers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HardcoverProblem",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "HardcoverProblemAt",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "ReadwiseProblem",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "ReadwiseProblemAt",
                table: "Readers");
        }
    }
}

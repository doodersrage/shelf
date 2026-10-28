using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.PostgresMigrations
{
    /// <inheritdoc />
    public partial class PostgresSyncProblems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HardcoverProblem",
                table: "Readers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HardcoverProblemAt",
                table: "Readers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReadwiseProblem",
                table: "Readers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReadwiseProblemAt",
                table: "Readers",
                type: "timestamp with time zone",
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

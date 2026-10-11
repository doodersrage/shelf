using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shelf.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Family : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GuardianId",
                table: "Readers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Restricted",
                table: "Readers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ForChildren",
                table: "Collections",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Readers_GuardianId",
                table: "Readers",
                column: "GuardianId");

            migrationBuilder.AddForeignKey(
                name: "FK_Readers_Readers_GuardianId",
                table: "Readers",
                column: "GuardianId",
                principalTable: "Readers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Readers_Readers_GuardianId",
                table: "Readers");

            migrationBuilder.DropIndex(
                name: "IX_Readers_GuardianId",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "GuardianId",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "Restricted",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "ForChildren",
                table: "Collections");
        }
    }
}

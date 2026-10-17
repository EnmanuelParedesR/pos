using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Host.Cloud.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StoreRncBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PreviousRnc",
                schema: "cloud",
                table: "Stores",
                type: "nvarchar(11)",
                maxLength: 11,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReportedRnc",
                schema: "cloud",
                table: "Stores",
                type: "nvarchar(11)",
                maxLength: 11,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RncNote",
                schema: "cloud",
                table: "Stores",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreviousRnc",
                schema: "cloud",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "ReportedRnc",
                schema: "cloud",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "RncNote",
                schema: "cloud",
                table: "Stores");
        }
    }
}

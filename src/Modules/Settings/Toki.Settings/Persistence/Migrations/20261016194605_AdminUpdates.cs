using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Settings.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UpdateAllowedVersion",
                schema: "settings",
                table: "Business",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdateAvailableVersion",
                schema: "settings",
                table: "Business",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdateChannel",
                schema: "settings",
                table: "Business",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdateNotesUrl",
                schema: "settings",
                table: "Business",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UpdateOutsideHours",
                schema: "settings",
                table: "Business",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdateRequestedUtc",
                schema: "settings",
                table: "Business",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RegisterVersions",
                schema: "settings",
                columns: table => new
                {
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PendingVersion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ReportedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RestartRequestedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegisterVersions", x => x.DeviceId);
                });
            // The new columns start null on the seeded row: no UpdateData (it would rewrite the business the owner edits).
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RegisterVersions",
                schema: "settings");

            migrationBuilder.DropColumn(
                name: "UpdateAllowedVersion",
                schema: "settings",
                table: "Business");

            migrationBuilder.DropColumn(
                name: "UpdateAvailableVersion",
                schema: "settings",
                table: "Business");

            migrationBuilder.DropColumn(
                name: "UpdateChannel",
                schema: "settings",
                table: "Business");

            migrationBuilder.DropColumn(
                name: "UpdateNotesUrl",
                schema: "settings",
                table: "Business");

            migrationBuilder.DropColumn(
                name: "UpdateOutsideHours",
                schema: "settings",
                table: "Business");

            migrationBuilder.DropColumn(
                name: "UpdateRequestedUtc",
                schema: "settings",
                table: "Business");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Host.Cloud.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HeartbeatLicense : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LicenseEndsUtc",
                schema: "replica",
                table: "NodeHeartbeats",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LicenseStatus",
                schema: "replica",
                table: "NodeHeartbeats",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LicenseEndsUtc",
                schema: "replica",
                table: "NodeHeartbeats");

            migrationBuilder.DropColumn(
                name: "LicenseStatus",
                schema: "replica",
                table: "NodeHeartbeats");
        }
    }
}

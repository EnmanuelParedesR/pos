using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Identity.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DevicePeripheralsIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PeripheralsReportedUtc",
                schema: "identity",
                table: "Devices",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PrinterWidth",
                schema: "identity",
                table: "Devices",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScaleProtocol",
                schema: "identity",
                table: "Devices",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PeripheralsReportedUtc",
                schema: "identity",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "PrinterWidth",
                schema: "identity",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "ScaleProtocol",
                schema: "identity",
                table: "Devices");
        }
    }
}

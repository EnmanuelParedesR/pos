using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Host.Lite.Migrations
{
    /// <inheritdoc />
    public partial class LiteSellingDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // TK-321: a device with a secret cannot prove itself any more; it pairs again with a code. None in production (deployLite was off).
            migrationBuilder.Sql("DELETE FROM [lite].[TenantDevices];");

            migrationBuilder.DropPrimaryKey(
                name: "PK_TenantDevices",
                schema: "lite",
                table: "TenantDevices");

            migrationBuilder.DropColumn(
                name: "SecretHash",
                schema: "lite",
                table: "TenantDevices");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                schema: "lite",
                table: "TenantDevices",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<byte[]>(
                name: "PublicKey",
                schema: "lite",
                table: "TenantDevices",
                type: "varbinary(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: Array.Empty<byte>());

            migrationBuilder.AddPrimaryKey(
                name: "PK_TenantDevices",
                schema: "lite",
                table: "TenantDevices",
                column: "DeviceId");

            migrationBuilder.CreateTable(
                name: "DeviceCodes",
                schema: "lite",
                columns: table => new
                {
                    CodeHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    BusinessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceCodes", x => x.CodeHash);
                });

            migrationBuilder.CreateTable(
                name: "DeviceNonces",
                schema: "lite",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(110)", maxLength: 110, nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceNonces", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCodes_BusinessId",
                schema: "lite",
                table: "DeviceCodes",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceNonces_ExpiresUtc",
                schema: "lite",
                table: "DeviceNonces",
                column: "ExpiresUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceCodes",
                schema: "lite");

            migrationBuilder.DropTable(
                name: "DeviceNonces",
                schema: "lite");

            migrationBuilder.DropPrimaryKey(
                name: "PK_TenantDevices",
                schema: "lite",
                table: "TenantDevices");

            migrationBuilder.DropColumn(
                name: "Name",
                schema: "lite",
                table: "TenantDevices");

            migrationBuilder.DropColumn(
                name: "PublicKey",
                schema: "lite",
                table: "TenantDevices");

            migrationBuilder.AddColumn<byte[]>(
                name: "SecretHash",
                schema: "lite",
                table: "TenantDevices",
                type: "varbinary(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: Array.Empty<byte>());

            migrationBuilder.AddPrimaryKey(
                name: "PK_TenantDevices",
                schema: "lite",
                table: "TenantDevices",
                column: "SecretHash");
        }
    }
}

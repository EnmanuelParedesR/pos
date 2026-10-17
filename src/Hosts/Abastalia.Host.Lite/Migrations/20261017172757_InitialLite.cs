using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Host.Lite.Migrations
{
    /// <inheritdoc />
    public partial class InitialLite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "lite");

            migrationBuilder.CreateTable(
                name: "TenantDevices",
                schema: "lite",
                columns: table => new
                {
                    SecretHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    BusinessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantDevices", x => x.SecretHash);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                schema: "lite",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantDevices_BusinessId",
                schema: "lite",
                table: "TenantDevices",
                column: "BusinessId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantDevices",
                schema: "lite");

            migrationBuilder.DropTable(
                name: "Tenants",
                schema: "lite");
        }
    }
}

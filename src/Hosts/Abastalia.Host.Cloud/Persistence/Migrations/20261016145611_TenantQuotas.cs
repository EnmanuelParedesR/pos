using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Host.Cloud.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantQuotas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AddOns",
                schema: "cloud",
                table: "Tenants",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Plan",
                schema: "cloud",
                table: "Tenants",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TenantUsage",
                schema: "cloud",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Month = table.Column<DateOnly>(type: "date", nullable: false),
                    Meter = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Count = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantUsage", x => new { x.TenantId, x.Month, x.Meter });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantUsage",
                schema: "cloud");

            migrationBuilder.DropColumn(
                name: "AddOns",
                schema: "cloud",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "Plan",
                schema: "cloud",
                table: "Tenants");
        }
    }
}

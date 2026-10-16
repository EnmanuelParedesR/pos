using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Host.Cloud.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlatformAdminAndHeartbeats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DisabledUtc",
                schema: "cloud",
                table: "Tenants",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RevokedUtc",
                schema: "cloud",
                table: "Nodes",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DailyUsage",
                schema: "replica",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StoreId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Sales = table.Column<int>(type: "int", nullable: false),
                    SalesAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    ActiveCashiers = table.Column<int>(type: "int", nullable: false),
                    Voids = table.Column<int>(type: "int", nullable: false),
                    Returns = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyUsage", x => new { x.TenantId, x.StoreId, x.Day });
                });

            migrationBuilder.CreateTable(
                name: "NodeHeartbeats",
                schema: "replica",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StoreId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AppVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Os = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UptimeMinutes = table.Column<int>(type: "int", nullable: false),
                    ActiveRegisters = table.Column<int>(type: "int", nullable: false),
                    OpenShifts = table.Column<int>(type: "int", nullable: false),
                    OutboxBacklog = table.Column<int>(type: "int", nullable: false),
                    LastErrorCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    OnAccount = table.Column<bool>(type: "bit", nullable: false),
                    ScaleConfigured = table.Column<bool>(type: "bit", nullable: true),
                    PrinterConfigured = table.Column<bool>(type: "bit", nullable: true),
                    FiscalProvider = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    BusinessNameSet = table.Column<bool>(type: "bit", nullable: false),
                    RncSet = table.Column<bool>(type: "bit", nullable: false),
                    ActiveProducts = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeHeartbeats", x => new { x.TenantId, x.NodeId });
                });

            migrationBuilder.CreateTable(
                name: "PlatformAudit",
                schema: "cloud",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Method = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Path = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformAudit", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAudit_AtUtc",
                schema: "cloud",
                table: "PlatformAudit",
                column: "AtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyUsage",
                schema: "replica");

            migrationBuilder.DropTable(
                name: "NodeHeartbeats",
                schema: "replica");

            migrationBuilder.DropTable(
                name: "PlatformAudit",
                schema: "cloud");

            migrationBuilder.DropColumn(
                name: "DisabledUtc",
                schema: "cloud",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "RevokedUtc",
                schema: "cloud",
                table: "Nodes");
        }
    }
}

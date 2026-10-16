using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Host.Cloud.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Billing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BillingEvents",
                schema: "cloud",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantSubscriptions",
                schema: "cloud",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaddleSubscriptionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PaddleCustomerId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Interval = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    PaidThroughUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NextBilledUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PastDueSinceUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastEventUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ManualUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ManualNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantSubscriptions", x => x.TenantId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BillingEvents_TenantId",
                schema: "cloud",
                table: "BillingEvents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantSubscriptions_PaddleSubscriptionId",
                schema: "cloud",
                table: "TenantSubscriptions",
                column: "PaddleSubscriptionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BillingEvents",
                schema: "cloud");

            migrationBuilder.DropTable(
                name: "TenantSubscriptions",
                schema: "cloud");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Host.Cloud.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AlertsAndCertification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CertificateExpiresUtc",
                schema: "replica",
                table: "NodeHeartbeats",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OldestPendingUtc",
                schema: "replica",
                table: "NodeHeartbeats",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RejectedWithoutReplacement",
                schema: "replica",
                table: "NodeHeartbeats",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AlertPreferences",
                schema: "cloud",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    DgiiRejected = table.Column<bool>(type: "bit", nullable: false),
                    CertificateExpiring = table.Column<bool>(type: "bit", nullable: false),
                    QueueStale = table.Column<bool>(type: "bit", nullable: false),
                    CertificationProgress = table.Column<bool>(type: "bit", nullable: false),
                    Unsubscribed = table.Column<bool>(type: "bit", nullable: false),
                    UnsubscribeToken = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertPreferences", x => new { x.TenantId, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "Certifications",
                schema: "replica",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StoreId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CurrentStep = table.Column<int>(type: "int", nullable: false),
                    TotalSteps = table.Column<int>(type: "int", nullable: false),
                    StepsDone = table.Column<int>(type: "int", nullable: false),
                    Certified = table.Column<bool>(type: "bit", nullable: false),
                    StepSinceUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ChangedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Certifications", x => new { x.TenantId, x.StoreId });
                });

            migrationBuilder.CreateTable(
                name: "SentAlerts",
                schema: "cloud",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StoreId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Recipients = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentAlerts", x => new { x.TenantId, x.StoreId, x.Kind, x.Day });
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertPreferences_UnsubscribeToken",
                schema: "cloud",
                table: "AlertPreferences",
                column: "UnsubscribeToken",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertPreferences",
                schema: "cloud");

            migrationBuilder.DropTable(
                name: "Certifications",
                schema: "replica");

            migrationBuilder.DropTable(
                name: "SentAlerts",
                schema: "cloud");

            migrationBuilder.DropColumn(
                name: "CertificateExpiresUtc",
                schema: "replica",
                table: "NodeHeartbeats");

            migrationBuilder.DropColumn(
                name: "OldestPendingUtc",
                schema: "replica",
                table: "NodeHeartbeats");

            migrationBuilder.DropColumn(
                name: "RejectedWithoutReplacement",
                schema: "replica",
                table: "NodeHeartbeats");
        }
    }
}

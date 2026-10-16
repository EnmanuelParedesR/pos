using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Host.Cloud.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class R11CloudSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LicenseJws",
                schema: "replica",
                table: "NodeHeartbeats",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SignerUnverified",
                schema: "cloud",
                table: "FeReceived",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "FeDelegations",
                schema: "cloud",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Rnc = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Thumbprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    HolderName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    RequestedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Approved = table.Column<bool>(type: "bit", nullable: true),
                    DecidedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    DecidedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeDelegations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NodeNonces",
                schema: "cloud",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(110)", maxLength: 110, nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeNonces", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Inbox_TenantId_ReceivedUtc",
                schema: "ingest",
                table: "Inbox",
                columns: new[] { "TenantId", "ReceivedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FeDelegations_TenantId_Rnc_Thumbprint",
                schema: "cloud",
                table: "FeDelegations",
                columns: new[] { "TenantId", "Rnc", "Thumbprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NodeNonces_ExpiresUtc",
                schema: "cloud",
                table: "NodeNonces",
                column: "ExpiresUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeDelegations",
                schema: "cloud");

            migrationBuilder.DropTable(
                name: "NodeNonces",
                schema: "cloud");

            migrationBuilder.DropIndex(
                name: "IX_Inbox_TenantId_ReceivedUtc",
                schema: "ingest",
                table: "Inbox");

            migrationBuilder.DropColumn(
                name: "LicenseJws",
                schema: "replica",
                table: "NodeHeartbeats");

            migrationBuilder.DropColumn(
                name: "SignerUnverified",
                schema: "cloud",
                table: "FeReceived");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Host.Cloud.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class R9ReceiverAndArchiveFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FeReceived_TenantId_IssuerRnc_ENcf",
                schema: "cloud",
                table: "FeReceived");

            migrationBuilder.DropIndex(
                name: "IX_FeCertificates_Rnc",
                schema: "cloud",
                table: "FeCertificates");

            migrationBuilder.DropIndex(
                name: "IX_EcfRelays_TenantId_ENcf",
                schema: "cloud",
                table: "EcfRelays");

            migrationBuilder.AddColumn<bool>(
                name: "Conflict",
                schema: "cloud",
                table: "FeReceived",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Sha256",
                schema: "cloud",
                table: "FeReceived",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "AiUsage",
                schema: "cloud",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    SupplierInvoices = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUsage", x => new { x.TenantId, x.Day });
                });

            migrationBuilder.CreateIndex(
                name: "IX_FeReceived_TenantId_Environment_IssuerRnc_ENcf_Sha256",
                schema: "cloud",
                table: "FeReceived",
                columns: new[] { "TenantId", "Environment", "IssuerRnc", "ENcf", "Sha256" },
                unique: true);

            // R9 #6: one active receiver per RNC. Should a race have left two, the first consent keeps it (the rule until now) and the
            // later one is revoked; its vault key is no longer used and goes when that tenant consents again.
            migrationBuilder.Sql("""
                UPDATE c SET RevokedUtc = SYSUTCDATETIME(), RevokedBy = N'R9: otro receptor activo con el mismo RNC'
                FROM cloud.FeCertificates c
                WHERE c.RevokedUtc IS NULL
                  AND EXISTS (SELECT 1 FROM cloud.FeCertificates o WHERE o.Rnc = c.Rnc AND o.RevokedUtc IS NULL AND o.ConsentedUtc < c.ConsentedUtc);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_FeCertificates_Rnc",
                schema: "cloud",
                table: "FeCertificates",
                column: "Rnc",
                unique: true,
                filter: "[RevokedUtc] IS NULL");

            // R9 #4: the archive key gains the environment. Existing rows were already one per e-NCF, so nothing needs splitting: an XML that a
            // test environment overwrote before this cannot be recovered. Their cold keys (without the environment) stay valid, since each row keeps its own.
            migrationBuilder.CreateIndex(
                name: "IX_EcfRelays_TenantId_Environment_ENcf",
                schema: "cloud",
                table: "EcfRelays",
                columns: new[] { "TenantId", "Environment", "ENcf" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiUsage",
                schema: "cloud");

            migrationBuilder.DropIndex(
                name: "IX_FeReceived_TenantId_Environment_IssuerRnc_ENcf_Sha256",
                schema: "cloud",
                table: "FeReceived");

            migrationBuilder.DropIndex(
                name: "IX_FeCertificates_Rnc",
                schema: "cloud",
                table: "FeCertificates");

            migrationBuilder.DropIndex(
                name: "IX_EcfRelays_TenantId_Environment_ENcf",
                schema: "cloud",
                table: "EcfRelays");

            migrationBuilder.DropColumn(
                name: "Conflict",
                schema: "cloud",
                table: "FeReceived");

            migrationBuilder.DropColumn(
                name: "Sha256",
                schema: "cloud",
                table: "FeReceived");

            migrationBuilder.CreateIndex(
                name: "IX_FeReceived_TenantId_IssuerRnc_ENcf",
                schema: "cloud",
                table: "FeReceived",
                columns: new[] { "TenantId", "IssuerRnc", "ENcf" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FeCertificates_Rnc",
                schema: "cloud",
                table: "FeCertificates",
                column: "Rnc");

            migrationBuilder.CreateIndex(
                name: "IX_EcfRelays_TenantId_ENcf",
                schema: "cloud",
                table: "EcfRelays",
                columns: new[] { "TenantId", "ENcf" },
                unique: true);
        }
    }
}

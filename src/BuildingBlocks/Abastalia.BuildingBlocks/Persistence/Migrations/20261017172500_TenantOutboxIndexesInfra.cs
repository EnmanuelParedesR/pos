using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.BuildingBlocks.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantOutboxIndexesInfra : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_ProcessedUtc",
                schema: "infra",
                table: "OutboxMessages");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_UploadedUtc",
                schema: "infra",
                table: "OutboxMessages");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_TenantId_ProcessedUtc",
                schema: "infra",
                table: "OutboxMessages",
                columns: new[] { "TenantId", "ProcessedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_TenantId_UploadedUtc",
                schema: "infra",
                table: "OutboxMessages",
                columns: new[] { "TenantId", "UploadedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_TenantId_ProcessedUtc",
                schema: "infra",
                table: "OutboxMessages");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_TenantId_UploadedUtc",
                schema: "infra",
                table: "OutboxMessages");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ProcessedUtc",
                schema: "infra",
                table: "OutboxMessages",
                column: "ProcessedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_UploadedUtc",
                schema: "infra",
                table: "OutboxMessages",
                column: "UploadedUtc");
        }
    }
}

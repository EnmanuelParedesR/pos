using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.BuildingBlocks.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OutboxUploadedIndexInfra : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_UploadedUtc",
                schema: "infra",
                table: "OutboxMessages",
                column: "UploadedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_UploadedUtc",
                schema: "infra",
                table: "OutboxMessages");
        }
    }
}

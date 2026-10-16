using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Host.Cloud.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AccompanimentOneOpen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AccompanimentRequests_OneOpen",
                schema: "cloud",
                table: "AccompanimentRequests",
                columns: new[] { "TenantId", "StoreId" },
                unique: true,
                filter: "[Status] <> 'Certified' AND [Status] <> 'Cancelled'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AccompanimentRequests_OneOpen",
                schema: "cloud",
                table: "AccompanimentRequests");
        }
    }
}

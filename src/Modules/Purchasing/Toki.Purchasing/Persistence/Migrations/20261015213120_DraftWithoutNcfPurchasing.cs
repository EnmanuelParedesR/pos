using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Purchasing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DraftWithoutNcfPurchasing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Purchases_SupplierId_Ncf",
                schema: "purchasing",
                table: "Purchases");

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_SupplierId_Ncf",
                schema: "purchasing",
                table: "Purchases",
                columns: new[] { "SupplierId", "Ncf" },
                unique: true,
                filter: "[Status] <> 'Voided' AND [Ncf] <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Purchases_SupplierId_Ncf",
                schema: "purchasing",
                table: "Purchases");

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_SupplierId_Ncf",
                schema: "purchasing",
                table: "Purchases",
                columns: new[] { "SupplierId", "Ncf" },
                unique: true,
                filter: "[Status] <> 'Voided'");
        }
    }
}

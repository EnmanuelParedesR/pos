using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Sales.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ENcfIndexesSales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Sales_ENcf",
                schema: "sales",
                table: "Sales",
                column: "ENcf");

            migrationBuilder.CreateIndex(
                name: "IX_Sales_VoidENcf",
                schema: "sales",
                table: "Sales",
                column: "VoidENcf");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sales_ENcf",
                schema: "sales",
                table: "Sales");

            migrationBuilder.DropIndex(
                name: "IX_Sales_VoidENcf",
                schema: "sales",
                table: "Sales");
        }
    }
}

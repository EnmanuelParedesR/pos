using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Inventory.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MaximumStockInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "MaximumStock",
                schema: "inventory",
                table: "Products",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaximumStock",
                schema: "inventory",
                table: "Products");
        }
    }
}

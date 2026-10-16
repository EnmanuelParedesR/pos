using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Inventory.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SampleDataInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSample",
                schema: "inventory",
                table: "Products",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsSample",
                schema: "inventory",
                table: "Categories",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsSample",
                schema: "inventory",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "IsSample",
                schema: "inventory",
                table: "Categories");
        }
    }
}

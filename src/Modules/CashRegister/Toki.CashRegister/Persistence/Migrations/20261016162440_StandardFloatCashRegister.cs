using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.CashRegister.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StandardFloatCashRegister : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "StandardFloat",
                schema: "cash",
                table: "Registers",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StandardFloat",
                schema: "cash",
                table: "Registers");
        }
    }
}

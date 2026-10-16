using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.CashRegister.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ShiftBlindMisses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BlindMisses",
                schema: "cash",
                table: "Shifts",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BlindMisses",
                schema: "cash",
                table: "Shifts");
        }
    }
}

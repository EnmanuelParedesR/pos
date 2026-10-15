using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Compras.Persistence.Migraciones
{
    /// <inheritdoc />
    public partial class MedioPagoCompras : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Medio",
                schema: "compras",
                table: "Gastos",
                type: "nvarchar(15)",
                maxLength: 15,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Medio",
                schema: "compras",
                table: "Compras",
                type: "nvarchar(15)",
                maxLength: 15,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Medio",
                schema: "compras",
                table: "Gastos");

            migrationBuilder.DropColumn(
                name: "Medio",
                schema: "compras",
                table: "Compras");
        }
    }
}

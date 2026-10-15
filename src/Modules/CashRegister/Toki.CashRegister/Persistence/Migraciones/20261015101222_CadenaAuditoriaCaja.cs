using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.CashRegister.Persistence.Migraciones
{
    /// <inheritdoc />
    public partial class CadenaAuditoriaCaja : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CadenaAuditoria",
                schema: "caja",
                table: "Turnos",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CadenaAuditoria",
                schema: "caja",
                table: "Turnos");
        }
    }
}

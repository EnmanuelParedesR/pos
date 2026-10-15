using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Configuracion.Persistence.Migraciones
{
    /// <inheritdoc />
    public partial class FraseRespaldoConfiguracion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RespaldoClave",
                schema: "config",
                table: "Negocio",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RespaldoSal",
                schema: "config",
                table: "Negocio",
                type: "varbinary(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.UpdateData(
                schema: "config",
                table: "Negocio",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "RespaldoClave", "RespaldoSal" },
                values: new object[] { null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RespaldoClave",
                schema: "config",
                table: "Negocio");

            migrationBuilder.DropColumn(
                name: "RespaldoSal",
                schema: "config",
                table: "Negocio");
        }
    }
}

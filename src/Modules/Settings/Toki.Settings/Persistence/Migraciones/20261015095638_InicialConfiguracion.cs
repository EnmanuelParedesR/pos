using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Settings.Persistence.Migraciones
{
    /// <inheritdoc />
    public partial class InicialConfiguracion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "config");

            migrationBuilder.CreateTable(
                name: "Negocio",
                schema: "config",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Nombre = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Rnc = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Direccion = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Telefono = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PieRecibo = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TopeDescuentoPct = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Negocio", x => x.Id);
                });

            migrationBuilder.InsertData(
                schema: "config",
                table: "Negocio",
                columns: new[] { "Id", "Direccion", "Nombre", "PieRecibo", "Rnc", "Telefono", "TopeDescuentoPct" },
                values: new object[] { 1, null, "Mi tienda", null, null, null, 10m });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Negocio",
                schema: "config");
        }
    }
}

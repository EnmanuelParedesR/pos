using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Fiscal.Persistence.Migraciones
{
    /// <inheritdoc />
    public partial class InicialFiscal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "fiscal");

            migrationBuilder.CreateTable(
                name: "Contribuyentes",
                schema: "fiscal",
                columns: table => new
                {
                    Rnc = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: false),
                    Nombre = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    NombreComercial = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    Activo = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contribuyentes", x => x.Rnc);
                });

            migrationBuilder.CreateTable(
                name: "DocumentosEcf",
                schema: "fiscal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ENcf = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: false),
                    Tipo = table.Column<int>(type: "int", nullable: false),
                    ReferenciaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ENcfModificado = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: true),
                    RncComprador = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: true),
                    NombreComprador = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Itbis = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    FechaFirmaUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CodigoSeguridad = table.Column<string>(type: "nvarchar(6)", maxLength: 6, nullable: false),
                    XmlFirmado = table.Column<byte[]>(type: "varbinary(max)", maxLength: 32000, nullable: false),
                    Estado = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FechaLimiteEnvioUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TrackId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Intentos = table.Column<int>(type: "int", nullable: false),
                    ProximoIntentoUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UltimoError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentosEcf", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SecuenciasEcf",
                schema: "fiscal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Tipo = table.Column<int>(type: "int", nullable: false),
                    Desde = table.Column<long>(type: "bigint", nullable: false),
                    Hasta = table.Column<long>(type: "bigint", nullable: false),
                    Siguiente = table.Column<long>(type: "bigint", nullable: false),
                    VenceEl = table.Column<DateOnly>(type: "date", nullable: false),
                    CreadaUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecuenciasEcf", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentosEcf_ENcf",
                schema: "fiscal",
                table: "DocumentosEcf",
                column: "ENcf",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentosEcf_Estado_FechaLimiteEnvioUtc",
                schema: "fiscal",
                table: "DocumentosEcf",
                columns: new[] { "Estado", "FechaLimiteEnvioUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentosEcf_ReferenciaId",
                schema: "fiscal",
                table: "DocumentosEcf",
                column: "ReferenciaId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Contribuyentes",
                schema: "fiscal");

            migrationBuilder.DropTable(
                name: "DocumentosEcf",
                schema: "fiscal");

            migrationBuilder.DropTable(
                name: "SecuenciasEcf",
                schema: "fiscal");
        }
    }
}

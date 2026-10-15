using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.CashRegister.Persistence.Migraciones
{
    /// <inheritdoc />
    public partial class InicialCaja : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "caja");

            migrationBuilder.CreateTable(
                name: "CajasFisicas",
                schema: "caja",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Nombre = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Activa = table.Column<bool>(type: "bit", nullable: false),
                    DispositivoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CajasFisicas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MovimientosCaja",
                schema: "caja",
                columns: table => new
                {
                    Secuencia = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TurnoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Tipo = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Medio = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Monto = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Motivo = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Nota = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReferenciaId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Referencia = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    UsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SupervisorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FechaUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovimientosCaja", x => x.Secuencia);
                });

            migrationBuilder.CreateTable(
                name: "Turnos",
                schema: "caja",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CajaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CajeroId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CajeroNombre = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AbiertoUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FondoInicial = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Estado = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    CerradoUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EfectivoEsperado = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    EfectivoContado = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    Diferencia = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    SupervisorCierreId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Conteo = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    NumeroZ = table.Column<int>(type: "int", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Turnos", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MovimientosCaja_Id",
                schema: "caja",
                table: "MovimientosCaja",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovimientosCaja_ReferenciaId",
                schema: "caja",
                table: "MovimientosCaja",
                column: "ReferenciaId");

            migrationBuilder.CreateIndex(
                name: "IX_MovimientosCaja_TurnoId",
                schema: "caja",
                table: "MovimientosCaja",
                column: "TurnoId");

            migrationBuilder.CreateIndex(
                name: "IX_Turnos_AbiertoUtc",
                schema: "caja",
                table: "Turnos",
                column: "AbiertoUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Turnos_CajaAbierta",
                schema: "caja",
                table: "Turnos",
                column: "CajaId",
                unique: true,
                filter: "[CerradoUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Turnos_CajeroAbierto",
                schema: "caja",
                table: "Turnos",
                column: "CajeroId",
                unique: true,
                filter: "[CerradoUtc] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CajasFisicas",
                schema: "caja");

            migrationBuilder.DropTable(
                name: "MovimientosCaja",
                schema: "caja");

            migrationBuilder.DropTable(
                name: "Turnos",
                schema: "caja");
        }
    }
}

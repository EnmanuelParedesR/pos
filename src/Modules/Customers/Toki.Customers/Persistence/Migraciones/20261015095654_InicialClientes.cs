using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Customers.Persistence.Migraciones
{
    /// <inheritdoc />
    public partial class InicialClientes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "clientes");

            migrationBuilder.CreateTable(
                name: "AbonosFiado",
                schema: "clientes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClienteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Monto = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Origen = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    Medio = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    TurnoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReferenciaId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Referencia = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    UsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Anulado = table.Column<bool>(type: "bit", nullable: false),
                    AnuladoUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MotivoAnulacion = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AbonosFiado", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CargosFiado",
                schema: "clientes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClienteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VentaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Documento = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Monto = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Anulado = table.Column<bool>(type: "bit", nullable: false),
                    AnuladoUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PagadoAlAnular = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CargosFiado", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Clientes",
                schema: "clientes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Nombre = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    RncCedula = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: true),
                    Telefono = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Direccion = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreditoFiscal = table.Column<bool>(type: "bit", nullable: false),
                    Nivel = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    LimiteCredito = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Activo = table.Column<bool>(type: "bit", nullable: false),
                    CreadoUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clientes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MovimientosSaldoFavor",
                schema: "clientes",
                columns: table => new
                {
                    Secuencia = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClienteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Tipo = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Monto = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    ReferenciaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovimientosSaldoFavor", x => x.Secuencia);
                });

            migrationBuilder.CreateTable(
                name: "AplicacionesAbono",
                schema: "clientes",
                columns: table => new
                {
                    AbonoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CargoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Monto = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AplicacionesAbono", x => new { x.AbonoId, x.CargoId });
                    table.ForeignKey(
                        name: "FK_AplicacionesAbono_AbonosFiado_AbonoId",
                        column: x => x.AbonoId,
                        principalSchema: "clientes",
                        principalTable: "AbonosFiado",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AbonosFiado_ClienteId",
                schema: "clientes",
                table: "AbonosFiado",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_AplicacionesAbono_CargoId",
                schema: "clientes",
                table: "AplicacionesAbono",
                column: "CargoId");

            migrationBuilder.CreateIndex(
                name: "IX_CargosFiado_ClienteId",
                schema: "clientes",
                table: "CargosFiado",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_CargosFiado_VentaId",
                schema: "clientes",
                table: "CargosFiado",
                column: "VentaId");

            migrationBuilder.CreateIndex(
                name: "IX_Clientes_Nombre",
                schema: "clientes",
                table: "Clientes",
                column: "Nombre");

            migrationBuilder.CreateIndex(
                name: "IX_Clientes_RncCedula",
                schema: "clientes",
                table: "Clientes",
                column: "RncCedula",
                unique: true,
                filter: "[RncCedula] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MovimientosSaldoFavor_ClienteId",
                schema: "clientes",
                table: "MovimientosSaldoFavor",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_MovimientosSaldoFavor_ReferenciaId",
                schema: "clientes",
                table: "MovimientosSaldoFavor",
                column: "ReferenciaId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AplicacionesAbono",
                schema: "clientes");

            migrationBuilder.DropTable(
                name: "CargosFiado",
                schema: "clientes");

            migrationBuilder.DropTable(
                name: "Clientes",
                schema: "clientes");

            migrationBuilder.DropTable(
                name: "MovimientosSaldoFavor",
                schema: "clientes");

            migrationBuilder.DropTable(
                name: "AbonosFiado",
                schema: "clientes");
        }
    }
}

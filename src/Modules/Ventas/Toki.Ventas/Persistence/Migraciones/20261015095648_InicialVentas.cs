using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Ventas.Persistence.Migraciones
{
    /// <inheritdoc />
    public partial class InicialVentas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ventas");

            migrationBuilder.CreateTable(
                name: "Devoluciones",
                schema: "ventas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VentaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TurnoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Medio = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Itbis = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    ENcf = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: false),
                    Motivo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupervisorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devoluciones", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Ventas",
                schema: "ventas",
                columns: table => new
                {
                    Numero = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TurnoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CajaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Caja = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CajeroId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Cajero = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ClienteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClienteNombre = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RncCliente = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: true),
                    TipoComprobante = table.Column<int>(type: "int", nullable: false),
                    ENcf = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: false),
                    CodigoSeguridad = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UrlQr = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Nivel = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Bruto = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Descuento = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Subtotal = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Itbis = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Cambio = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Redondeo = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Estado = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AnuladaUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MotivoAnulacion = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AnuladaPorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ENcfAnulacion = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SupervisorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ventas", x => x.Numero);
                    table.UniqueConstraint("AK_Ventas_Id", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VentasEnEspera",
                schema: "ventas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TurnoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Etiqueta = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Contenido = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    CreadaUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VentasEnEspera", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LineasDevolucion",
                schema: "ventas",
                columns: table => new
                {
                    DevolucionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineaVentaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Cantidad = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Itbis = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Repuesto = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LineasDevolucion", x => new { x.DevolucionId, x.LineaVentaId });
                    table.ForeignKey(
                        name: "FK_LineasDevolucion_Devoluciones_DevolucionId",
                        column: x => x.DevolucionId,
                        principalSchema: "ventas",
                        principalTable: "Devoluciones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LineasVenta",
                schema: "ventas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VentaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Orden = table.Column<int>(type: "int", nullable: false),
                    ProductoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PresentacionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CategoriaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Descripcion = table.Column<string>(type: "nvarchar(220)", maxLength: 220, nullable: false),
                    Unidad = table.Column<int>(type: "int", nullable: false),
                    Cantidad = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Factor = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    PrecioLista = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    PrecioUnitario = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Bruto = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Descuento = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    TasaItbis = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    Itbis = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    CostoUnitario = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Nota = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CodigoEtiqueta = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CantidadDevuelta = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LineasVenta", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LineasVenta_Ventas_VentaId",
                        column: x => x.VentaId,
                        principalSchema: "ventas",
                        principalTable: "Ventas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PagosVenta",
                schema: "ventas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VentaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Medio = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Monto = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Recibido = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    Referencia = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PagosVenta", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PagosVenta_Ventas_VentaId",
                        column: x => x.VentaId,
                        principalSchema: "ventas",
                        principalTable: "Ventas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Devoluciones_VentaId",
                schema: "ventas",
                table: "Devoluciones",
                column: "VentaId");

            migrationBuilder.CreateIndex(
                name: "IX_LineasVenta_ProductoId",
                schema: "ventas",
                table: "LineasVenta",
                column: "ProductoId");

            migrationBuilder.CreateIndex(
                name: "IX_LineasVenta_VentaId",
                schema: "ventas",
                table: "LineasVenta",
                column: "VentaId");

            migrationBuilder.CreateIndex(
                name: "IX_PagosVenta_VentaId",
                schema: "ventas",
                table: "PagosVenta",
                column: "VentaId");

            migrationBuilder.CreateIndex(
                name: "IX_Ventas_ClienteId",
                schema: "ventas",
                table: "Ventas",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_Ventas_FechaUtc",
                schema: "ventas",
                table: "Ventas",
                column: "FechaUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Ventas_TurnoId",
                schema: "ventas",
                table: "Ventas",
                column: "TurnoId");

            migrationBuilder.CreateIndex(
                name: "IX_VentasEnEspera_TurnoId",
                schema: "ventas",
                table: "VentasEnEspera",
                column: "TurnoId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LineasDevolucion",
                schema: "ventas");

            migrationBuilder.DropTable(
                name: "LineasVenta",
                schema: "ventas");

            migrationBuilder.DropTable(
                name: "PagosVenta",
                schema: "ventas");

            migrationBuilder.DropTable(
                name: "VentasEnEspera",
                schema: "ventas");

            migrationBuilder.DropTable(
                name: "Devoluciones",
                schema: "ventas");

            migrationBuilder.DropTable(
                name: "Ventas",
                schema: "ventas");
        }
    }
}

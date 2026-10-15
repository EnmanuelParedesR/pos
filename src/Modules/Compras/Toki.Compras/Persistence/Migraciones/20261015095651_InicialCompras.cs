using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Compras.Persistence.Migraciones
{
    /// <inheritdoc />
    public partial class InicialCompras : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "compras");

            migrationBuilder.CreateTable(
                name: "Compras",
                schema: "compras",
                columns: table => new
                {
                    Numero = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SuplidorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SuplidorNombre = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RncSuplidor = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: true),
                    Ncf = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: false),
                    FechaFactura = table.Column<DateOnly>(type: "date", nullable: false),
                    Condicion = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Vence = table.Column<DateOnly>(type: "date", nullable: true),
                    AlmacenId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Estado = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    Subtotal = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Itbis = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Notas = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreadaUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AplicadaUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AnuladaUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MotivoAnulacion = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    UsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Compras", x => x.Numero);
                    table.UniqueConstraint("AK_Compras_Id", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CuentasPorPagar",
                schema: "compras",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SuplidorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Tipo = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    DocumentoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ncf = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: false),
                    Fecha = table.Column<DateOnly>(type: "date", nullable: false),
                    Vence = table.Column<DateOnly>(type: "date", nullable: false),
                    Total = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Anulada = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CuentasPorPagar", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Gastos",
                schema: "compras",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SuplidorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SuplidorNombre = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RncSuplidor = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: true),
                    Ncf = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: false),
                    Fecha = table.Column<DateOnly>(type: "date", nullable: false),
                    Concepto = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Tipo = table.Column<int>(type: "int", nullable: false),
                    Condicion = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Vence = table.Column<DateOnly>(type: "date", nullable: true),
                    Subtotal = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Itbis = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Anulado = table.Column<bool>(type: "bit", nullable: false),
                    MotivoAnulacion = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreadoUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Gastos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PagosSuplidor",
                schema: "compras",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SuplidorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Fecha = table.Column<DateOnly>(type: "date", nullable: false),
                    Monto = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Medio = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    Referencia = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    TurnoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreadoUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Anulado = table.Column<bool>(type: "bit", nullable: false),
                    MotivoAnulacion = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PagosSuplidor", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Suplidores",
                schema: "compras",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Nombre = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Rnc = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: true),
                    Telefono = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Contacto = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DiasCredito = table.Column<int>(type: "int", nullable: false),
                    Activo = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Suplidores", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LineasCompra",
                schema: "compras",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompraId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Orden = table.Column<int>(type: "int", nullable: false),
                    ProductoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Descripcion = table.Column<string>(type: "nvarchar(220)", maxLength: 220, nullable: false),
                    Cantidad = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Factor = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    CostoUnitario = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    TasaItbis = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    Subtotal = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Itbis = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LineasCompra", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LineasCompra_Compras_CompraId",
                        column: x => x.CompraId,
                        principalSchema: "compras",
                        principalTable: "Compras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AplicacionesPago",
                schema: "compras",
                columns: table => new
                {
                    PagoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CuentaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Monto = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AplicacionesPago", x => new { x.PagoId, x.CuentaId });
                    table.ForeignKey(
                        name: "FK_AplicacionesPago_PagosSuplidor_PagoId",
                        column: x => x.PagoId,
                        principalSchema: "compras",
                        principalTable: "PagosSuplidor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AplicacionesPago_CuentaId",
                schema: "compras",
                table: "AplicacionesPago",
                column: "CuentaId");

            migrationBuilder.CreateIndex(
                name: "IX_Compras_FechaFactura",
                schema: "compras",
                table: "Compras",
                column: "FechaFactura");

            migrationBuilder.CreateIndex(
                name: "IX_Compras_SuplidorId_Ncf",
                schema: "compras",
                table: "Compras",
                columns: new[] { "SuplidorId", "Ncf" },
                unique: true,
                filter: "[Estado] <> 'Anulada'");

            migrationBuilder.CreateIndex(
                name: "IX_CuentasPorPagar_DocumentoId",
                schema: "compras",
                table: "CuentasPorPagar",
                column: "DocumentoId");

            migrationBuilder.CreateIndex(
                name: "IX_CuentasPorPagar_SuplidorId",
                schema: "compras",
                table: "CuentasPorPagar",
                column: "SuplidorId");

            migrationBuilder.CreateIndex(
                name: "IX_Gastos_Fecha",
                schema: "compras",
                table: "Gastos",
                column: "Fecha");

            migrationBuilder.CreateIndex(
                name: "IX_Gastos_SuplidorId_Ncf",
                schema: "compras",
                table: "Gastos",
                columns: new[] { "SuplidorId", "Ncf" },
                unique: true,
                filter: "[Anulado] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_LineasCompra_CompraId",
                schema: "compras",
                table: "LineasCompra",
                column: "CompraId");

            migrationBuilder.CreateIndex(
                name: "IX_PagosSuplidor_SuplidorId",
                schema: "compras",
                table: "PagosSuplidor",
                column: "SuplidorId");

            migrationBuilder.CreateIndex(
                name: "IX_Suplidores_Nombre",
                schema: "compras",
                table: "Suplidores",
                column: "Nombre");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AplicacionesPago",
                schema: "compras");

            migrationBuilder.DropTable(
                name: "CuentasPorPagar",
                schema: "compras");

            migrationBuilder.DropTable(
                name: "Gastos",
                schema: "compras");

            migrationBuilder.DropTable(
                name: "LineasCompra",
                schema: "compras");

            migrationBuilder.DropTable(
                name: "Suplidores",
                schema: "compras");

            migrationBuilder.DropTable(
                name: "PagosSuplidor",
                schema: "compras");

            migrationBuilder.DropTable(
                name: "Compras",
                schema: "compras");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Toki.Inventario.Persistence.Migraciones
{
    /// <inheritdoc />
    public partial class InicialInventario : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "inventario");

            migrationBuilder.CreateTable(
                name: "Almacenes",
                schema: "inventario",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Nombre = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Activo = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Almacenes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Categorias",
                schema: "inventario",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Nombre = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Color = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    Notas = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Activa = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categorias", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CodigosBarras",
                schema: "inventario",
                columns: table => new
                {
                    Codigo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ProductoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PresentacionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CodigosBarras", x => x.Codigo);
                });

            migrationBuilder.CreateTable(
                name: "ConteosFisicos",
                schema: "inventario",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AlmacenId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CategoriaId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Estado = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreadoUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AplicadoUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConteosFisicos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Existencias",
                schema: "inventario",
                columns: table => new
                {
                    ProductoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AlmacenId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Cantidad = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    PendienteContar = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Existencias", x => new { x.ProductoId, x.AlmacenId });
                });

            migrationBuilder.CreateTable(
                name: "FormatosEtiqueta",
                schema: "inventario",
                columns: table => new
                {
                    Prefijo = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    LongitudPlu = table.Column<int>(type: "int", nullable: false),
                    EsPrecio = table.Column<bool>(type: "bit", nullable: false),
                    Decimales = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FormatosEtiqueta", x => x.Prefijo);
                });

            migrationBuilder.CreateTable(
                name: "MovimientosInventario",
                schema: "inventario",
                columns: table => new
                {
                    Secuencia = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AlmacenId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Tipo = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Cantidad = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    CostoUnitario = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    ExistenciaResultante = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    ReferenciaId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Motivo = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    UsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FechaUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovimientosInventario", x => x.Secuencia);
                });

            migrationBuilder.CreateTable(
                name: "Presentaciones",
                schema: "inventario",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Nombre = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Factor = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Precio = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    PrecioMayorista = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    Activa = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Presentaciones", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Productos",
                schema: "inventario",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Nombre = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false, collation: "Latin1_General_100_CI_AI"),
                    CategoriaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Unidad = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Plu = table.Column<string>(type: "nvarchar(6)", maxLength: 6, nullable: true),
                    TasaItbis = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    Precio = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    PrecioMayorista = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    Costo = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    VenderSinExistencia = table.Column<bool>(type: "bit", nullable: false),
                    StockMinimo = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Activo = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Productos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConteosFisicosLineas",
                schema: "inventario",
                columns: table => new
                {
                    ConteoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Contado = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    SistemaAlContar = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConteosFisicosLineas", x => new { x.ConteoId, x.ProductoId });
                    table.ForeignKey(
                        name: "FK_ConteosFisicosLineas_ConteosFisicos_ConteoId",
                        column: x => x.ConteoId,
                        principalSchema: "inventario",
                        principalTable: "ConteosFisicos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                schema: "inventario",
                table: "Almacenes",
                columns: new[] { "Id", "Activo", "Nombre" },
                values: new object[] { new Guid("0199e000-0000-7000-8000-0000000000a1"), true, "Tienda" });

            migrationBuilder.InsertData(
                schema: "inventario",
                table: "FormatosEtiqueta",
                columns: new[] { "Prefijo", "Decimales", "EsPrecio", "LongitudPlu" },
                values: new object[,]
                {
                    { "20", 3, false, 5 },
                    { "21", 3, false, 5 },
                    { "22", 2, true, 4 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_CodigosBarras_ProductoId",
                schema: "inventario",
                table: "CodigosBarras",
                column: "ProductoId");

            migrationBuilder.CreateIndex(
                name: "IX_MovimientosInventario_Id",
                schema: "inventario",
                table: "MovimientosInventario",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovimientosInventario_ProductoId_Secuencia",
                schema: "inventario",
                table: "MovimientosInventario",
                columns: new[] { "ProductoId", "Secuencia" });

            migrationBuilder.CreateIndex(
                name: "IX_MovimientosInventario_ReferenciaId",
                schema: "inventario",
                table: "MovimientosInventario",
                column: "ReferenciaId");

            migrationBuilder.CreateIndex(
                name: "IX_Presentaciones_ProductoId",
                schema: "inventario",
                table: "Presentaciones",
                column: "ProductoId");

            migrationBuilder.CreateIndex(
                name: "IX_Productos_Nombre",
                schema: "inventario",
                table: "Productos",
                column: "Nombre");

            migrationBuilder.CreateIndex(
                name: "IX_Productos_Plu",
                schema: "inventario",
                table: "Productos",
                column: "Plu",
                unique: true,
                filter: "[Plu] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Almacenes",
                schema: "inventario");

            migrationBuilder.DropTable(
                name: "Categorias",
                schema: "inventario");

            migrationBuilder.DropTable(
                name: "CodigosBarras",
                schema: "inventario");

            migrationBuilder.DropTable(
                name: "ConteosFisicosLineas",
                schema: "inventario");

            migrationBuilder.DropTable(
                name: "Existencias",
                schema: "inventario");

            migrationBuilder.DropTable(
                name: "FormatosEtiqueta",
                schema: "inventario");

            migrationBuilder.DropTable(
                name: "MovimientosInventario",
                schema: "inventario");

            migrationBuilder.DropTable(
                name: "Presentaciones",
                schema: "inventario");

            migrationBuilder.DropTable(
                name: "Productos",
                schema: "inventario");

            migrationBuilder.DropTable(
                name: "ConteosFisicos",
                schema: "inventario");
        }
    }
}

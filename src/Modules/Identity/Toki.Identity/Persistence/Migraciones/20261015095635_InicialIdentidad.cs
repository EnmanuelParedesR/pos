using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Toki.Identity.Persistence.Migraciones
{
    /// <inheritdoc />
    public partial class InicialIdentidad : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "identidad");

            migrationBuilder.CreateTable(
                name: "AutorizacionesSupervisor",
                schema: "identidad",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupervisorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SolicitanteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DispositivoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Permiso = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RecursoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpiraUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UsadaUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutorizacionesSupervisor", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CodigosEmparejamiento",
                schema: "identidad",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NombreDispositivo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CodigoHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    ExpiraUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UsadoUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CodigosEmparejamiento", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dispositivos",
                schema: "identidad",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Nombre = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TokenHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    Revocado = table.Column<bool>(type: "bit", nullable: false),
                    CreadoUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dispositivos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                schema: "identidad",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Nombre = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Permisos = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sesiones",
                schema: "identidad",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DispositivoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    CreadaUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiraUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CerradaUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sesiones", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Usuarios",
                schema: "identidad",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Nombre = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Codigo = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PinHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    RolId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Activo = table.Column<bool>(type: "bit", nullable: false),
                    IntentosFallidos = table.Column<int>(type: "int", nullable: false),
                    BloqueadoHastaUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Usuarios", x => x.Id);
                });

            migrationBuilder.InsertData(
                schema: "identidad",
                table: "Roles",
                columns: new[] { "Id", "Nombre", "Permisos" },
                values: new object[,]
                {
                    { new Guid("0199e000-0000-7000-8000-000000000001"), "Administrador", "usuarios.gestionar,dispositivos.gestionar,configuracion.gestionar,productos.gestionar,precios.cambiar,inventario.ver,inventario.ajustar,inventario.contar,inventario.transferir,caja.operar,caja.retirar,caja.aprobar-cierre,caja.abrir-gaveta,ventas.registrar,ventas.anular,ventas.devolver,ventas.descuento-mayor,ventas.cambiar-precio,compras.gestionar,clientes.gestionar,fiado.gestionar,fiado.exceder-limite,fiscal.gestionar,reportes.ver" },
                    { new Guid("0199e000-0000-7000-8000-000000000002"), "Supervisor", "productos.gestionar,precios.cambiar,inventario.ver,inventario.ajustar,inventario.contar,inventario.transferir,caja.operar,caja.retirar,caja.aprobar-cierre,caja.abrir-gaveta,ventas.registrar,ventas.anular,ventas.devolver,ventas.descuento-mayor,ventas.cambiar-precio,compras.gestionar,clientes.gestionar,fiado.gestionar,fiado.exceder-limite,reportes.ver" },
                    { new Guid("0199e000-0000-7000-8000-000000000003"), "Cajero", "ventas.registrar,caja.operar,inventario.ver,clientes.gestionar" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_CodigosEmparejamiento_CodigoHash",
                schema: "identidad",
                table: "CodigosEmparejamiento",
                column: "CodigoHash");

            migrationBuilder.CreateIndex(
                name: "IX_Dispositivos_TokenHash",
                schema: "identidad",
                table: "Dispositivos",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sesiones_TokenHash",
                schema: "identidad",
                table: "Sesiones",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Usuarios_Codigo",
                schema: "identidad",
                table: "Usuarios",
                column: "Codigo",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutorizacionesSupervisor",
                schema: "identidad");

            migrationBuilder.DropTable(
                name: "CodigosEmparejamiento",
                schema: "identidad");

            migrationBuilder.DropTable(
                name: "Dispositivos",
                schema: "identidad");

            migrationBuilder.DropTable(
                name: "Roles",
                schema: "identidad");

            migrationBuilder.DropTable(
                name: "Sesiones",
                schema: "identidad");

            migrationBuilder.DropTable(
                name: "Usuarios",
                schema: "identidad");
        }
    }
}

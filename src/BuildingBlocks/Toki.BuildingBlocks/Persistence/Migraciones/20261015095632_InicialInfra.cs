using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.BuildingBlocks.Persistence.Migraciones
{
    /// <inheritdoc />
    public partial class InicialInfra : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "infra");

            migrationBuilder.CreateTable(
                name: "AuditoriaCabeza",
                schema: "infra",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Secuencia = table.Column<long>(type: "bigint", nullable: false),
                    Hash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditoriaCabeza", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditoriaRegistros",
                schema: "infra",
                columns: table => new
                {
                    Secuencia = table.Column<long>(type: "bigint", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SupervisorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DispositivoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Accion = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RecursoTipo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RecursoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Datos = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    HashPrevio = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    Hash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditoriaRegistros", x => x.Secuencia);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMensajes",
                schema: "infra",
                columns: table => new
                {
                    Secuencia = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Tipo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AgregadoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    OcurridoUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DispositivoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProcesadoUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SubidoUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMensajes", x => x.Secuencia);
                });

            migrationBuilder.InsertData(
                schema: "infra",
                table: "AuditoriaCabeza",
                columns: new[] { "Id", "Hash", "Secuencia" },
                values: new object[] { 1, new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 0L });

            migrationBuilder.CreateIndex(
                name: "IX_AuditoriaRegistros_RecursoTipo_RecursoId",
                schema: "infra",
                table: "AuditoriaRegistros",
                columns: new[] { "RecursoTipo", "RecursoId" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMensajes_Id",
                schema: "infra",
                table: "OutboxMensajes",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMensajes_ProcesadoUtc",
                schema: "infra",
                table: "OutboxMensajes",
                column: "ProcesadoUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditoriaCabeza",
                schema: "infra");

            migrationBuilder.DropTable(
                name: "AuditoriaRegistros",
                schema: "infra");

            migrationBuilder.DropTable(
                name: "OutboxMensajes",
                schema: "infra");
        }
    }
}

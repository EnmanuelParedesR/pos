using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.BuildingBlocks.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialInfra : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "infra");

            migrationBuilder.CreateTable(
                name: "AuditHead",
                schema: "infra",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Hash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditHead", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditRecords",
                schema: "infra",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DateUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SupervisorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Data = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    PreviousHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    Hash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditRecords", x => x.Sequence);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                schema: "infra",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AggregateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProcessedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UploadedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Sequence);
                });

            migrationBuilder.InsertData(
                schema: "infra",
                table: "AuditHead",
                columns: new[] { "Id", "Hash", "Sequence" },
                values: new object[] { 1, new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 0L });

            migrationBuilder.CreateIndex(
                name: "IX_AuditRecords_ResourceType_ResourceId",
                schema: "infra",
                table: "AuditRecords",
                columns: new[] { "ResourceType", "ResourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Id",
                schema: "infra",
                table: "OutboxMessages",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ProcessedUtc",
                schema: "infra",
                table: "OutboxMessages",
                column: "ProcessedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditHead",
                schema: "infra");

            migrationBuilder.DropTable(
                name: "AuditRecords",
                schema: "infra");

            migrationBuilder.DropTable(
                name: "OutboxMessages",
                schema: "infra");
        }
    }
}

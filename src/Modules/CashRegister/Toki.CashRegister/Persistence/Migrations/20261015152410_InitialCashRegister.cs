using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.CashRegister.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCashRegister : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "cash");

            migrationBuilder.CreateTable(
                name: "CashMovements",
                schema: "cash",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShiftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Method = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReferenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Reference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SupervisorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DateUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashMovements", x => x.Sequence);
                });

            migrationBuilder.CreateTable(
                name: "Registers",
                schema: "cash",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Registers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Shifts",
                schema: "cash",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegisterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CashierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CashierName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OpenedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    OpeningFloat = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ClosedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExpectedCash = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    CountedCash = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    Difference = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    ClosingSupervisorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CashCount = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    ZNumber = table.Column<int>(type: "int", nullable: true),
                    AuditChainHead = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shifts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CashMovements_Id",
                schema: "cash",
                table: "CashMovements",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashMovements_ReferenceId",
                schema: "cash",
                table: "CashMovements",
                column: "ReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_CashMovements_ShiftId",
                schema: "cash",
                table: "CashMovements",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_Shifts_OpenCashier",
                schema: "cash",
                table: "Shifts",
                column: "CashierId",
                unique: true,
                filter: "[ClosedUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Shifts_OpenedUtc",
                schema: "cash",
                table: "Shifts",
                column: "OpenedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Shifts_OpenRegister",
                schema: "cash",
                table: "Shifts",
                column: "RegisterId",
                unique: true,
                filter: "[ClosedUtc] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CashMovements",
                schema: "cash");

            migrationBuilder.DropTable(
                name: "Registers",
                schema: "cash");

            migrationBuilder.DropTable(
                name: "Shifts",
                schema: "cash");
        }
    }
}

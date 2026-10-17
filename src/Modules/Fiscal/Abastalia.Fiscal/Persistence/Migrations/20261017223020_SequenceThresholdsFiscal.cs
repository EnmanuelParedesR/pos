using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Fiscal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SequenceThresholdsFiscal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EcfSequenceThresholds",
                schema: "fiscal",
                columns: table => new
                {
                    Type = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Percent = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EcfSequenceThresholds", x => new { x.TenantId, x.Type });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EcfSequenceThresholds",
                schema: "fiscal");
        }
    }
}

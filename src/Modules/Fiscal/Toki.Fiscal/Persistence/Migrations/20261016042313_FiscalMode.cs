using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Fiscal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FiscalMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Mode",
                schema: "fiscal",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ChangedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Mode", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Mode",
                schema: "fiscal");
        }
    }
}

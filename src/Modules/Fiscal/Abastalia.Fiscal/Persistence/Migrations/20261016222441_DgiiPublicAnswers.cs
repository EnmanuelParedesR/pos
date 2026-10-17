using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Fiscal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DgiiPublicAnswers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DgiiPublicAnswers",
                schema: "fiscal",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Answer = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DgiiPublicAnswers", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DgiiPublicAnswers_ExpiresUtc",
                schema: "fiscal",
                table: "DgiiPublicAnswers",
                column: "ExpiresUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DgiiPublicAnswers",
                schema: "fiscal");
        }
    }
}

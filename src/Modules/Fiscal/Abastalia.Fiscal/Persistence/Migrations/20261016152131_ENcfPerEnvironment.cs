using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Fiscal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ENcfPerEnvironment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EcfDocuments_ENcf",
                schema: "fiscal",
                table: "EcfDocuments");

            migrationBuilder.CreateIndex(
                name: "IX_EcfDocuments_Environment_ENcf",
                schema: "fiscal",
                table: "EcfDocuments",
                columns: new[] { "Environment", "ENcf" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EcfDocuments_Environment_ENcf",
                schema: "fiscal",
                table: "EcfDocuments");

            migrationBuilder.CreateIndex(
                name: "IX_EcfDocuments_ENcf",
                schema: "fiscal",
                table: "EcfDocuments",
                column: "ENcf",
                unique: true);
        }
    }
}

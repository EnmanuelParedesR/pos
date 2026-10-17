using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Fiscal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class R12FiscalIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_EcfDocuments_ArchivedUtc",
                schema: "fiscal",
                table: "EcfDocuments",
                column: "ArchivedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_EcfDocuments_ResultUtc",
                schema: "fiscal",
                table: "EcfDocuments",
                column: "ResultUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EcfDocuments_ArchivedUtc",
                schema: "fiscal",
                table: "EcfDocuments");

            migrationBuilder.DropIndex(
                name: "IX_EcfDocuments_ResultUtc",
                schema: "fiscal",
                table: "EcfDocuments");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Fiscal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalSignedAtStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "ApprovalXmlGzip",
                schema: "fiscal",
                table: "EcfReceived",
                type: "varbinary(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovalXmlGzip",
                schema: "fiscal",
                table: "EcfReceived");
        }
    }
}

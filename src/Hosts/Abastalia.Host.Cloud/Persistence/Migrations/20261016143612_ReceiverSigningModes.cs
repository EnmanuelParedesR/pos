using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Host.Cloud.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReceiverSigningModes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AcknowledgedUtc",
                schema: "cloud",
                table: "FeReceived",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SigningMode",
                schema: "cloud",
                table: "FeCertificates",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "CloudCustody"); // TK-246: the receivers from before hold a vault copy (TK-151)

            migrationBuilder.Sql("UPDATE [cloud].[FeReceived] SET [AcknowledgedUtc] = [ReceivedUtc]"); // every one before TK-246 was acknowledged on arrival

            migrationBuilder.CreateTable(
                name: "FeReceiverOutages",
                schema: "cloud",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ToUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeReceiverOutages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FeReceived_AcknowledgedUtc",
                schema: "cloud",
                table: "FeReceived",
                column: "AcknowledgedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_FeReceiverOutages_TenantId_NodeId_ToUtc",
                schema: "cloud",
                table: "FeReceiverOutages",
                columns: new[] { "TenantId", "NodeId", "ToUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeReceiverOutages",
                schema: "cloud");

            migrationBuilder.DropIndex(
                name: "IX_FeReceived_AcknowledgedUtc",
                schema: "cloud",
                table: "FeReceived");

            migrationBuilder.DropColumn(
                name: "AcknowledgedUtc",
                schema: "cloud",
                table: "FeReceived");

            migrationBuilder.DropColumn(
                name: "SigningMode",
                schema: "cloud",
                table: "FeCertificates");
        }
    }
}

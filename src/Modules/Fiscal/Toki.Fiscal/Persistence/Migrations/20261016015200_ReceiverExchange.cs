using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Fiscal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReceiverExchange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EcfDeliveries",
                schema: "fiscal",
                columns: table => new
                {
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ENcf = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: false),
                    BuyerRnc = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Message = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AckXml = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    DeliveredUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EcfDeliveries", x => x.DocumentId);
                });

            migrationBuilder.CreateTable(
                name: "EcfReceived",
                schema: "fiscal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CloudId = table.Column<long>(type: "bigint", nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    IssuerRnc = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: false),
                    IssuerName = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    ENcf = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    IssuedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Total = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Itbis = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    XmlGzip = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Approval = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    ApprovalReason = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    ApprovalUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ApprovalDeliveredUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ApprovalDgiiUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ApprovalMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EcfReceived", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EcfDeliveries_Status_NextAttemptUtc",
                schema: "fiscal",
                table: "EcfDeliveries",
                columns: new[] { "Status", "NextAttemptUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EcfReceived_CloudId",
                schema: "fiscal",
                table: "EcfReceived",
                column: "CloudId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EcfReceived_ReceivedUtc",
                schema: "fiscal",
                table: "EcfReceived",
                column: "ReceivedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EcfDeliveries",
                schema: "fiscal");

            migrationBuilder.DropTable(
                name: "EcfReceived",
                schema: "fiscal");
        }
    }
}

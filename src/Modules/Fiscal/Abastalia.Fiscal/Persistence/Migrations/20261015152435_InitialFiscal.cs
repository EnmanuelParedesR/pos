using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Fiscal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialFiscal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "fiscal");

            migrationBuilder.CreateTable(
                name: "EcfDocuments",
                schema: "fiscal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ENcf = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    ReferenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModifiedENcf = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: true),
                    BuyerRnc = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: true),
                    BuyerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Itbis = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    SignedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SecurityCode = table.Column<string>(type: "nvarchar(6)", maxLength: 6, nullable: false),
                    SignedXml = table.Column<byte[]>(type: "varbinary(max)", maxLength: 32000, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SubmitDeadlineUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TrackId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EcfDocuments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EcfSequences",
                schema: "fiscal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    From = table.Column<long>(type: "bigint", nullable: false),
                    To = table.Column<long>(type: "bigint", nullable: false),
                    Next = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EcfSequences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Taxpayers",
                schema: "fiscal",
                columns: table => new
                {
                    Rnc = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    TradeName = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Taxpayers", x => x.Rnc);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EcfDocuments_ENcf",
                schema: "fiscal",
                table: "EcfDocuments",
                column: "ENcf",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EcfDocuments_ReferenceId",
                schema: "fiscal",
                table: "EcfDocuments",
                column: "ReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_EcfDocuments_Status_SubmitDeadlineUtc",
                schema: "fiscal",
                table: "EcfDocuments",
                columns: new[] { "Status", "SubmitDeadlineUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EcfDocuments",
                schema: "fiscal");

            migrationBuilder.DropTable(
                name: "EcfSequences",
                schema: "fiscal");

            migrationBuilder.DropTable(
                name: "Taxpayers",
                schema: "fiscal");
        }
    }
}

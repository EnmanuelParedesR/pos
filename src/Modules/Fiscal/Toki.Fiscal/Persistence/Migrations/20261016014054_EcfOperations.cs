using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Fiscal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EcfOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CertificationCases",
                schema: "fiscal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Row = table.Column<int>(type: "int", nullable: false),
                    ENcf = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    SignedXml = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    RfceXml = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    SecurityCode = table.Column<string>(type: "nvarchar(6)", maxLength: 6, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TrackId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Messages = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResultUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CertificationCases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CertificationSteps",
                schema: "fiscal",
                columns: table => new
                {
                    Step = table.Column<int>(type: "int", nullable: false),
                    Done = table.Column<bool>(type: "bit", nullable: false),
                    DoneUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CertificationSteps", x => x.Step);
                });

            migrationBuilder.CreateTable(
                name: "Contingencies",
                schema: "fiscal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contingencies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaperDocuments",
                schema: "fiscal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ncf = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    ReferenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContingencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IssuedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Total = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Data = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RegularizedByENcf = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: true),
                    RegularizedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperDocuments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaperSequences",
                schema: "fiscal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    From = table.Column<long>(type: "bigint", nullable: false),
                    To = table.Column<long>(type: "bigint", nullable: false),
                    Next = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperSequences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaperDocuments_Ncf",
                schema: "fiscal",
                table: "PaperDocuments",
                column: "Ncf",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CertificationCases",
                schema: "fiscal");

            migrationBuilder.DropTable(
                name: "CertificationSteps",
                schema: "fiscal");

            migrationBuilder.DropTable(
                name: "Contingencies",
                schema: "fiscal");

            migrationBuilder.DropTable(
                name: "PaperDocuments",
                schema: "fiscal");

            migrationBuilder.DropTable(
                name: "PaperSequences",
                schema: "fiscal");
        }
    }
}

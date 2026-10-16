using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Host.Cloud.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FeReceiver : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FeApprovalsReceived",
                schema: "cloud",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ENcf = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: false),
                    BuyerRnc = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: false),
                    Approved = table.Column<bool>(type: "bit", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    XmlGzip = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeApprovalsReceived", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FeCertificates",
                schema: "cloud",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Rnc = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Thumbprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NotAfterUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    KeyName = table.Column<string>(type: "nvarchar(127)", maxLength: 127, nullable: false),
                    OwnerName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    HolderName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    ConsentVersion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ConsentedByNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConsentedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RevokedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeCertificates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FeReceived",
                schema: "cloud",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    IssuerRnc = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: false),
                    IssuerName = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    BuyerRnc = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: false),
                    ENcf = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    IssuedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Total = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Itbis = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    XmlGzip = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Approval = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    ApprovalReason = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    ApprovalXmlGzip = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    ApprovalUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ApprovalDeliveredUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ApprovalDgiiUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ApprovalMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeReceived", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FeSignatures",
                schema: "cloud",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CertificateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Root = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ENcf = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: true),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    XmlGzip = table.Column<byte[]>(type: "varbinary(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeSignatures", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FeApprovalsReceived_TenantId_ENcf",
                schema: "cloud",
                table: "FeApprovalsReceived",
                columns: new[] { "TenantId", "ENcf" });

            migrationBuilder.CreateIndex(
                name: "IX_FeCertificates_Rnc",
                schema: "cloud",
                table: "FeCertificates",
                column: "Rnc");

            migrationBuilder.CreateIndex(
                name: "IX_FeCertificates_TenantId",
                schema: "cloud",
                table: "FeCertificates",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_FeReceived_TenantId_Id",
                schema: "cloud",
                table: "FeReceived",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_FeReceived_TenantId_IssuerRnc_ENcf",
                schema: "cloud",
                table: "FeReceived",
                columns: new[] { "TenantId", "IssuerRnc", "ENcf" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FeSignatures_TenantId_AtUtc",
                schema: "cloud",
                table: "FeSignatures",
                columns: new[] { "TenantId", "AtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeApprovalsReceived",
                schema: "cloud");

            migrationBuilder.DropTable(
                name: "FeCertificates",
                schema: "cloud");

            migrationBuilder.DropTable(
                name: "FeReceived",
                schema: "cloud");

            migrationBuilder.DropTable(
                name: "FeSignatures",
                schema: "cloud");
        }
    }
}

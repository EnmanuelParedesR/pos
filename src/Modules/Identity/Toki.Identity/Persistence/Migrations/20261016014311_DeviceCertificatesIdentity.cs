using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Identity.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeviceCertificatesIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CertificateThumbprint",
                schema: "identity",
                table: "Devices",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_CertificateThumbprint",
                schema: "identity",
                table: "Devices",
                column: "CertificateThumbprint",
                unique: true,
                filter: "[CertificateThumbprint] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Devices_CertificateThumbprint",
                schema: "identity",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "CertificateThumbprint",
                schema: "identity",
                table: "Devices");
        }
    }
}

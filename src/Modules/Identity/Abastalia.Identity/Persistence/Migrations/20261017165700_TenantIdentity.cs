using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Abastalia.Identity.Persistence.Migrations
{
    /// <summary>TK-319: TenantId on every table (the store's rows get the fixed store tenant); keys and unique indexes that two businesses could
    /// repeat lead with it. EF also wrote a delete and re-insert of the seed rows because their key changed: removed by hand, the store keeps
    /// what the owner edited.</summary>
    public partial class TenantIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_Code",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Sessions_TokenHash",
                schema: "identity",
                table: "Sessions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Roles",
                schema: "identity",
                table: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_Devices_CertificateThumbprint",
                schema: "identity",
                table: "Devices");

            migrationBuilder.DropIndex(
                name: "IX_Devices_TokenHash",
                schema: "identity",
                table: "Devices");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "identity",
                table: "Users",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "identity",
                table: "SupervisorApprovals",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "identity",
                table: "Sessions",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "identity",
                table: "Roles",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "identity",
                table: "PairingCodes",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "identity",
                table: "Devices",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddPrimaryKey(
                name: "PK_Roles",
                schema: "identity",
                table: "Roles",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_TenantId_Code",
                schema: "identity",
                table: "Users",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_TenantId_TokenHash",
                schema: "identity",
                table: "Sessions",
                columns: new[] { "TenantId", "TokenHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_TenantId_CertificateThumbprint",
                schema: "identity",
                table: "Devices",
                columns: new[] { "TenantId", "CertificateThumbprint" },
                unique: true,
                filter: "[CertificateThumbprint] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_TenantId_TokenHash",
                schema: "identity",
                table: "Devices",
                columns: new[] { "TenantId", "TokenHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_TenantId_Code",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Sessions_TenantId_TokenHash",
                schema: "identity",
                table: "Sessions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Roles",
                schema: "identity",
                table: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_Devices_TenantId_CertificateThumbprint",
                schema: "identity",
                table: "Devices");

            migrationBuilder.DropIndex(
                name: "IX_Devices_TenantId_TokenHash",
                schema: "identity",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "identity",
                table: "SupervisorApprovals");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "identity",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "identity",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "identity",
                table: "PairingCodes");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "identity",
                table: "Devices");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Roles",
                schema: "identity",
                table: "Roles",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Code",
                schema: "identity",
                table: "Users",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_TokenHash",
                schema: "identity",
                table: "Sessions",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_CertificateThumbprint",
                schema: "identity",
                table: "Devices",
                column: "CertificateThumbprint",
                unique: true,
                filter: "[CertificateThumbprint] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_TokenHash",
                schema: "identity",
                table: "Devices",
                column: "TokenHash",
                unique: true);
        }
    }
}

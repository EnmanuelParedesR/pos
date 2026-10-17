using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Purchasing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantPurchasing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Purchases_SupplierId_Ncf",
                schema: "purchasing",
                table: "Purchases");

            migrationBuilder.DropIndex(
                name: "IX_Expenses_SupplierId_Ncf",
                schema: "purchasing",
                table: "Expenses");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "purchasing",
                table: "Suppliers",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "purchasing",
                table: "SupplierPayments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "purchasing",
                table: "Purchases",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "purchasing",
                table: "PurchaseLines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "purchasing",
                table: "PurchaseFiles",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "purchasing",
                table: "PaymentAllocations",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "purchasing",
                table: "Payables",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "purchasing",
                table: "Expenses",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_TenantId_SupplierId_Ncf",
                schema: "purchasing",
                table: "Purchases",
                columns: new[] { "TenantId", "SupplierId", "Ncf" },
                unique: true,
                filter: "[Status] <> 'Voided' AND [Ncf] <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_TenantId_SupplierId_Ncf",
                schema: "purchasing",
                table: "Expenses",
                columns: new[] { "TenantId", "SupplierId", "Ncf" },
                unique: true,
                filter: "[IsVoided] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Purchases_TenantId_SupplierId_Ncf",
                schema: "purchasing",
                table: "Purchases");

            migrationBuilder.DropIndex(
                name: "IX_Expenses_TenantId_SupplierId_Ncf",
                schema: "purchasing",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "purchasing",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "purchasing",
                table: "SupplierPayments");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "purchasing",
                table: "Purchases");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "purchasing",
                table: "PurchaseLines");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "purchasing",
                table: "PurchaseFiles");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "purchasing",
                table: "PaymentAllocations");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "purchasing",
                table: "Payables");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "purchasing",
                table: "Expenses");

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_SupplierId_Ncf",
                schema: "purchasing",
                table: "Purchases",
                columns: new[] { "SupplierId", "Ncf" },
                unique: true,
                filter: "[Status] <> 'Voided' AND [Ncf] <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_SupplierId_Ncf",
                schema: "purchasing",
                table: "Expenses",
                columns: new[] { "SupplierId", "Ncf" },
                unique: true,
                filter: "[IsVoided] = 0");
        }
    }
}

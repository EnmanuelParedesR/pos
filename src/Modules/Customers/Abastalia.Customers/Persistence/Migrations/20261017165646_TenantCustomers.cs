using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Customers.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantCustomers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Customers_TaxId",
                schema: "customers",
                table: "Customers");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "customers",
                table: "OnAccountCharges",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "customers",
                table: "Customers",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "customers",
                table: "CustomerPayments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "customers",
                table: "CustomerPaymentApplications",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "customers",
                table: "CreditBalanceMovements",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TenantId_TaxId",
                schema: "customers",
                table: "Customers",
                columns: new[] { "TenantId", "TaxId" },
                unique: true,
                filter: "[TaxId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Customers_TenantId_TaxId",
                schema: "customers",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "customers",
                table: "OnAccountCharges");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "customers",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "customers",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "customers",
                table: "CustomerPaymentApplications");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "customers",
                table: "CreditBalanceMovements");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TaxId",
                schema: "customers",
                table: "Customers",
                column: "TaxId",
                unique: true,
                filter: "[TaxId] IS NOT NULL");
        }
    }
}

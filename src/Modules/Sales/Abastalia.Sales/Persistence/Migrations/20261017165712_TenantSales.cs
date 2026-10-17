using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Sales.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantSales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_CatalogSettings",
                schema: "sales",
                table: "CatalogSettings");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "sales",
                table: "Sales",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "sales",
                table: "SalePayments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "sales",
                table: "SaleLines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "sales",
                table: "Returns",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "sales",
                table: "ReturnLines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "sales",
                table: "Orders",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "sales",
                table: "HeldSales",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "sales",
                table: "CatalogSettings",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "sales",
                table: "CatalogItems",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddPrimaryKey(
                name: "PK_CatalogSettings",
                schema: "sales",
                table: "CatalogSettings",
                columns: new[] { "TenantId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_CatalogSettings",
                schema: "sales",
                table: "CatalogSettings");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "sales",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "sales",
                table: "SalePayments");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "sales",
                table: "SaleLines");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "sales",
                table: "Returns");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "sales",
                table: "ReturnLines");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "sales",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "sales",
                table: "HeldSales");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "sales",
                table: "CatalogSettings");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "sales",
                table: "CatalogItems");

            migrationBuilder.AddPrimaryKey(
                name: "PK_CatalogSettings",
                schema: "sales",
                table: "CatalogSettings",
                column: "Id");
        }
    }
}

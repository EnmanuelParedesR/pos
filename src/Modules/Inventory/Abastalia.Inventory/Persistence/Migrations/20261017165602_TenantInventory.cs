using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Abastalia.Inventory.Persistence.Migrations
{
    /// <summary>TK-319: TenantId on every table (the store's rows get the fixed store tenant); keys and unique indexes that two businesses could
    /// repeat lead with it. EF also wrote a delete and re-insert of the seed rows because their key changed: removed by hand, the store keeps
    /// what the owner edited.</summary>
    public partial class TenantInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_Warehouses",
                schema: "inventory",
                table: "Warehouses");

            migrationBuilder.DropIndex(
                name: "IX_Products_Plu",
                schema: "inventory",
                table: "Products");

            migrationBuilder.DropPrimaryKey(
                name: "PK_LabelFormats",
                schema: "inventory",
                table: "LabelFormats");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Barcodes",
                schema: "inventory",
                table: "Barcodes");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "inventory",
                table: "Warehouses",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "inventory",
                table: "StockMovements",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "inventory",
                table: "StockCounts",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "inventory",
                table: "StockCountLines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "inventory",
                table: "Stock",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "inventory",
                table: "Products",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "inventory",
                table: "Presentations",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "inventory",
                table: "LabelFormats",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "inventory",
                table: "Categories",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "inventory",
                table: "Barcodes",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddPrimaryKey(
                name: "PK_Warehouses",
                schema: "inventory",
                table: "Warehouses",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_LabelFormats",
                schema: "inventory",
                table: "LabelFormats",
                columns: new[] { "TenantId", "Prefix" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_Barcodes",
                schema: "inventory",
                table: "Barcodes",
                columns: new[] { "TenantId", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_Products_TenantId_Plu",
                schema: "inventory",
                table: "Products",
                columns: new[] { "TenantId", "Plu" },
                unique: true,
                filter: "[Plu] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_Warehouses",
                schema: "inventory",
                table: "Warehouses");

            migrationBuilder.DropIndex(
                name: "IX_Products_TenantId_Plu",
                schema: "inventory",
                table: "Products");

            migrationBuilder.DropPrimaryKey(
                name: "PK_LabelFormats",
                schema: "inventory",
                table: "LabelFormats");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Barcodes",
                schema: "inventory",
                table: "Barcodes");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "inventory",
                table: "Warehouses");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "inventory",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "inventory",
                table: "StockCounts");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "inventory",
                table: "StockCountLines");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "inventory",
                table: "Stock");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "inventory",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "inventory",
                table: "Presentations");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "inventory",
                table: "LabelFormats");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "inventory",
                table: "Categories");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "inventory",
                table: "Barcodes");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Warehouses",
                schema: "inventory",
                table: "Warehouses",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_LabelFormats",
                schema: "inventory",
                table: "LabelFormats",
                column: "Prefix");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Barcodes",
                schema: "inventory",
                table: "Barcodes",
                column: "Code");

            migrationBuilder.CreateIndex(
                name: "IX_Products_Plu",
                schema: "inventory",
                table: "Products",
                column: "Plu",
                unique: true,
                filter: "[Plu] IS NOT NULL");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Sales.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FiscalReceiptFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Contingency",
                schema: "sales",
                table: "Sales",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateOnly>(
                name: "SequenceExpiresOn",
                schema: "sales",
                table: "Sales",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SignedUtc",
                schema: "sales",
                table: "Sales",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Contingency",
                schema: "sales",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "SequenceExpiresOn",
                schema: "sales",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "SignedUtc",
                schema: "sales",
                table: "Sales");
        }
    }
}

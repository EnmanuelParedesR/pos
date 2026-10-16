using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Purchasing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PurchaseLinePresentationPurchasing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PresentationId",
                schema: "purchasing",
                table: "PurchaseLines",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PresentationId",
                schema: "purchasing",
                table: "PurchaseLines");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Identity.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CostsViewIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                schema: "identity",
                table: "Roles",
                keyColumn: "Id",
                keyValue: new Guid("0199e000-0000-7000-8000-000000000001"),
                column: "Permissions",
                value: "users.manage,devices.manage,settings.manage,products.manage,prices.change,inventory.view,inventory.adjust,inventory.count,inventory.transfer,register.operate,register.withdraw,register.approve-close,register.open-drawer,sales.create,sales.void,sales.return,sales.large-discount,sales.price-override,sales.remove-line,purchasing.manage,customers.manage,credit.manage,credit.exceed-limit,fiscal.manage,reports.view,costs.view");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                schema: "identity",
                table: "Roles",
                keyColumn: "Id",
                keyValue: new Guid("0199e000-0000-7000-8000-000000000001"),
                column: "Permissions",
                value: "users.manage,devices.manage,settings.manage,products.manage,prices.change,inventory.view,inventory.adjust,inventory.count,inventory.transfer,register.operate,register.withdraw,register.approve-close,register.open-drawer,sales.create,sales.void,sales.return,sales.large-discount,sales.price-override,sales.remove-line,purchasing.manage,customers.manage,credit.manage,credit.exceed-limit,fiscal.manage,reports.view");
        }
    }
}

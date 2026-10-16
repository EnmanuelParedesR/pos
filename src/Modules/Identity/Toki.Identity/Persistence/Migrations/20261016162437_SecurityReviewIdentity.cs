using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Identity.Persistence.Migrations
{
    /// <summary>R11 (TK-253): approvals bound to their content (PayloadHash), PIN failures that decay (LastFailedUtc), the idle lock (LastSeenUtc),
    /// and the seeded roles of docs/permisos.md: Cajero and Cajero con apertura without customers.manage and inventory.view (with customers.quick-add),
    /// Supervisor without payables.pay (with credit.sell and customers.quick-add). Written with SQL, like SegmentedPermissionsIdentity (R6 #9): only a
    /// seeded role the owner never edited (exactly its old permissions, in any order) changes; an edited one keeps what it has. A role that may manage
    /// credit gets credit.sell, so nobody who sold on credit loses it.</summary>
    public partial class SecurityReviewIdentity : Migration
    {
        internal static readonly (Guid Id, string[] Old, string New)[] Seeded =
        [
            (new Guid("0199e000-0000-7000-8000-000000000002"),
                ["products.manage", "prices.change", "inventory.view", "inventory.adjust", "inventory.count", "inventory.transfer", "register.open", "register.close",
                 "register.cash-in", "register.withdraw", "register.approve-close", "register.open-drawer", "sales.create", "sales.void", "sales.return",
                 "sales.large-discount", "sales.price-override", "sales.remove-line", "purchasing.manage", "payables.pay", "customers.manage", "credit.collect",
                 "credit.manage", "credit.exceed-limit", "reports.view", "shifts.view", "fiscal.reports"],
                "products.manage,prices.change,inventory.view,inventory.adjust,inventory.count,inventory.transfer,register.open,register.close,register.cash-in,register.withdraw,register.approve-close,register.open-drawer,sales.create,sales.void,sales.return,sales.large-discount,sales.price-override,sales.remove-line,purchasing.manage,customers.manage,customers.quick-add,credit.collect,credit.sell,credit.manage,credit.exceed-limit,reports.view,shifts.view,fiscal.reports"),
            (new Guid("0199e000-0000-7000-8000-000000000003"),
                ["sales.create", "register.close", "register.cash-in", "credit.collect", "inventory.view", "customers.manage"],
                "sales.create,register.close,register.cash-in,credit.collect,customers.quick-add"),
            (new Guid("0199e000-0000-7000-8000-000000000004"),
                ["sales.create", "register.open", "register.close", "register.cash-in", "credit.collect", "inventory.view", "customers.manage"],
                "sales.create,register.open,register.close,register.cash-in,credit.collect,customers.quick-add"),
        ];

        /// <summary>Sets <paramref name="value"/> only on the role whose permissions are exactly <paramref name="set"/> (same length, each one present).</summary>
        internal static string WhenUntouched(Guid id, string[] set, string value) =>
            $"UPDATE [identity].[Roles] SET Permissions = '{value}' WHERE Id = '{id}' AND LEN(Permissions) = {set.Sum(p => p.Length) + set.Length - 1}"
            + string.Concat(set.Select(p => $" AND ',' + Permissions + ',' LIKE '%,{p},%'"));

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastFailedUtc",
                schema: "identity",
                table: "Users",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "PayloadHash",
                schema: "identity",
                table: "SupervisorApprovals",
                type: "varbinary(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSeenUtc",
                schema: "identity",
                table: "Sessions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.UpdateData(
                schema: "identity",
                table: "Roles",
                keyColumn: "Id",
                keyValue: new Guid("0199e000-0000-7000-8000-000000000001"),
                column: "Permissions",
                value: "users.manage,devices.manage,settings.manage,registers.manage,backups.manage,audit.view,products.manage,prices.change,inventory.view,inventory.adjust,inventory.count,inventory.transfer,register.open,register.close,register.cash-in,register.withdraw,register.approve-close,register.open-drawer,sales.create,sales.void,sales.return,sales.large-discount,sales.price-override,sales.remove-line,purchasing.manage,payables.pay,customers.manage,customers.quick-add,credit.collect,credit.sell,credit.manage,credit.exceed-limit,fiscal.manage,fiscal.reports,reports.view,shifts.view,costs.view");

            foreach (var (id, old, value) in Seeded) migrationBuilder.Sql(WhenUntouched(id, old, value));
            migrationBuilder.Sql("""
                UPDATE [identity].[Roles] SET Permissions = Permissions + ',credit.sell'
                WHERE Id <> '0199E000-0000-7000-8000-000000000001'
                  AND ',' + Permissions + ',' LIKE '%,credit.manage,%' AND ',' + Permissions + ',' NOT LIKE '%,credit.sell,%'
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (id, old, value) in Seeded) migrationBuilder.Sql(WhenUntouched(id, value.Split(','), string.Join(',', old)));
            foreach (var p in new[] { "credit.sell", "customers.quick-add" })
                migrationBuilder.Sql($"""
                    UPDATE [identity].[Roles] SET Permissions = TRIM(',' FROM REPLACE(',' + Permissions + ',', ',{p},', ','))
                    WHERE ',' + Permissions + ',' LIKE '%,{p},%'
                    """);

            migrationBuilder.DropColumn(
                name: "LastFailedUtc",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PayloadHash",
                schema: "identity",
                table: "SupervisorApprovals");

            migrationBuilder.DropColumn(
                name: "LastSeenUtc",
                schema: "identity",
                table: "Sessions");

            migrationBuilder.UpdateData(
                schema: "identity",
                table: "Roles",
                keyColumn: "Id",
                keyValue: new Guid("0199e000-0000-7000-8000-000000000001"),
                column: "Permissions",
                value: "users.manage,devices.manage,settings.manage,registers.manage,backups.manage,audit.view,products.manage,prices.change,inventory.view,inventory.adjust,inventory.count,inventory.transfer,register.open,register.close,register.cash-in,register.withdraw,register.approve-close,register.open-drawer,sales.create,sales.void,sales.return,sales.large-discount,sales.price-override,sales.remove-line,purchasing.manage,payables.pay,customers.manage,credit.collect,credit.manage,credit.exceed-limit,fiscal.manage,fiscal.reports,reports.view,shifts.view,costs.view");
        }
    }
}

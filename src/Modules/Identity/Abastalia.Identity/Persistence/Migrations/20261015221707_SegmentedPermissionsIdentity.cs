using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Identity.Persistence.Migrations
{
    /// <summary>M16 (TK-212..216, docs/permisos.md): register.operate, settings.manage, reports.view and purchasing.manage are split.
    /// Roles the owner edits are written with SQL (R6 #9): each keeps exactly what it could do; the new template roles are added unless the store
    /// already has a role with that name. A store with no users yet (a new install) gets the new Cajero, which no longer opens the register.</summary>
    public partial class SegmentedPermissionsIdentity : Migration
    {
        private const string NewCashier = "sales.create,register.close,register.cash-in,credit.collect,inventory.view,customers.manage";

        private static readonly (Guid Id, string Name, string Permissions)[] Templates =
        [
            (new Guid("0199e000-0000-7000-8000-000000000004"), "Cajero con apertura", "sales.create,register.open,register.close,register.cash-in,credit.collect,inventory.view,customers.manage"),
            (new Guid("0199e000-0000-7000-8000-000000000005"), "Inventario", "products.manage,inventory.view,inventory.adjust,inventory.count,inventory.transfer,purchasing.manage"),
            (new Guid("0199e000-0000-7000-8000-000000000006"), "Contador", "reports.view,fiscal.reports,costs.view,shifts.view"),
        ];

        /// <summary>docs/permisos.md, "Migración sin sorpresas": a role that had an old, wide permission gets the new ones that cover the same actions,
        /// so nobody loses what they could do. A literal copy inside the migration (R8 #23): a migration never changes meaning with later code.</summary>
        internal static readonly (string Old, string[] New)[] Split =
        [
            ("register.operate", ["register.open", "register.close", "register.cash-in", "credit.collect"]),
            ("settings.manage", ["registers.manage", "backups.manage", "audit.view"]),
            ("reports.view", ["shifts.view", "fiscal.reports"]),
            ("purchasing.manage", ["payables.pay"]),
        ];

        /// <summary>Portable SQL (SQL Server 2017+ and Sqlite 3.44+): appends each new permission to every role that has the old one and lacks the new one.
        /// Administrador is left out: it always has everything.</summary>
        /// <param name="table">"[identity].[Roles]" on SQL Server; "Roles" on Sqlite, which has no schemas.</param>
        internal static string Expand(string table) => string.Concat(
            from m in Split
            from n in m.New
            select $"""
                UPDATE {table} SET Permissions = CONCAT(Permissions, ',{n}')
                WHERE Id <> '0199E000-0000-7000-8000-000000000001'
                  AND CONCAT(',', Permissions, ',') LIKE '%,{m.Old},%' AND CONCAT(',', Permissions, ',') NOT LIKE '%,{n},%';

                """);

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                schema: "identity",
                table: "Roles",
                keyColumn: "Id",
                keyValue: new Guid("0199e000-0000-7000-8000-000000000001"),
                column: "Permissions",
                value: "users.manage,devices.manage,settings.manage,registers.manage,backups.manage,audit.view,products.manage,prices.change,inventory.view,inventory.adjust,inventory.count,inventory.transfer,register.open,register.close,register.cash-in,register.withdraw,register.approve-close,register.open-drawer,sales.create,sales.void,sales.return,sales.large-discount,sales.price-override,sales.remove-line,purchasing.manage,payables.pay,customers.manage,credit.collect,credit.manage,credit.exceed-limit,fiscal.manage,fiscal.reports,reports.view,shifts.view,costs.view");

            migrationBuilder.Sql(Expand("[identity].[Roles]"));
            migrationBuilder.Sql("""
                UPDATE [identity].[Roles] SET Permissions = TRIM(',' FROM REPLACE(',' + Permissions + ',', ',register.operate,', ','))
                WHERE ',' + Permissions + ',' LIKE '%,register.operate,%'
                """);
            migrationBuilder.Sql($"""
                UPDATE [identity].[Roles] SET Permissions = '{NewCashier}'
                WHERE Id = '0199e000-0000-7000-8000-000000000003' AND NOT EXISTS (SELECT 1 FROM [identity].[Users])
                """);
            foreach (var (id, name, permissions) in Templates)
                migrationBuilder.Sql($"""
                    INSERT INTO [identity].[Roles] (Id, Name, Permissions) SELECT '{id}', N'{name}', '{permissions}'
                    WHERE NOT EXISTS (SELECT 1 FROM [identity].[Roles] WHERE Id = '{id}' OR Name = N'{name}')
                    """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (id, _, _) in Templates)
                migrationBuilder.Sql($"DELETE FROM [identity].[Roles] WHERE Id = '{id}' AND NOT EXISTS (SELECT 1 FROM [identity].[Users] WHERE RoleId = '{id}')");
            migrationBuilder.Sql("""
                UPDATE [identity].[Roles] SET Permissions = Permissions + ',register.operate'
                WHERE ',' + Permissions + ',' LIKE '%,register.open,%' OR ',' + Permissions + ',' LIKE '%,register.close,%'
                   OR ',' + Permissions + ',' LIKE '%,register.cash-in,%' OR ',' + Permissions + ',' LIKE '%,credit.collect,%'
                """);
            foreach (var (_, added) in Split)
                foreach (var p in added)
                    migrationBuilder.Sql($"""
                        UPDATE [identity].[Roles] SET Permissions = TRIM(',' FROM REPLACE(',' + Permissions + ',', ',{p},', ','))
                        WHERE ',' + Permissions + ',' LIKE '%,{p},%'
                        """);

            migrationBuilder.UpdateData(
                schema: "identity",
                table: "Roles",
                keyColumn: "Id",
                keyValue: new Guid("0199e000-0000-7000-8000-000000000001"),
                column: "Permissions",
                value: "users.manage,devices.manage,settings.manage,products.manage,prices.change,inventory.view,inventory.adjust,inventory.count,inventory.transfer,register.operate,register.withdraw,register.approve-close,register.open-drawer,sales.create,sales.void,sales.return,sales.large-discount,sales.price-override,sales.remove-line,purchasing.manage,customers.manage,credit.manage,credit.exceed-limit,fiscal.manage,reports.view,costs.view");
        }
    }
}

namespace Toki.Identity.Persistence;

/// <summary>M16 (docs/permisos.md, "Migración sin sorpresas"): a role that had an old, wide permission gets the new ones that cover the same actions,
/// so nobody loses what they could do. Used by the SegmentedPermissionsIdentity migration; literal on purpose (migrations never change meaning).</summary>
internal static class PermissionSplit
{
    public static readonly (string Old, string[] New)[] Map =
    [
        ("register.operate", ["register.open", "register.close", "register.cash-in", "credit.collect"]),
        ("settings.manage", ["registers.manage", "backups.manage", "audit.view"]),
        ("reports.view", ["shifts.view", "fiscal.reports"]),
        ("purchasing.manage", ["payables.pay"]),
    ];

    /// <summary>Portable SQL (SQL Server 2017+ and Sqlite 3.44+): appends each new permission to every role that has the old one and lacks the new one.
    /// Administrador is left out: it always has everything.</summary>
    /// <param name="table">"[identity].[Roles]" on SQL Server; "Roles" on Sqlite, which has no schemas.</param>
    public static string Expand(string table) => string.Concat(
        from m in Map
        from n in m.New
        select $"""
            UPDATE {table} SET Permissions = CONCAT(Permissions, ',{n}')
            WHERE Id <> '0199E000-0000-7000-8000-000000000001'
              AND CONCAT(',', Permissions, ',') LIKE '%,{m.Old},%' AND CONCAT(',', Permissions, ',') NOT LIKE '%,{n},%';

            """);
}

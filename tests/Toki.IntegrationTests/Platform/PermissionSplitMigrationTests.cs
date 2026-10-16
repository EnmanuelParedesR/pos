using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Shouldly;
using Toki.Identity.Persistence;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Platform;

/// <summary>M16 "Migración sin sorpresas": every role keeps what it could do when the wide permissions are split (SegmentedPermissionsIdentity).</summary>
public sealed class PermissionSplitMigrationTests
{
    private const string Admin = "0199e000-0000-7000-8000-000000000001", Cashier = "0199e000-0000-7000-8000-000000000003";

    /// <summary>The expansion SQL itself, on Sqlite (which creates its tables from the model and never runs migrations).</summary>
    [Fact]
    public async Task The_expansion_gives_each_role_the_new_permissions_of_the_old_ones_it_had()
    {
        await using var c = new SqliteConnection("Data Source=:memory:");
        await c.OpenAsync();
        await ExecAsync(c, $"""
            CREATE TABLE Roles (Id TEXT PRIMARY KEY, Name TEXT, Permissions TEXT);
            INSERT INTO Roles VALUES ('{Admin.ToUpperInvariant()}', 'Administrador', 'users.manage');
            INSERT INTO Roles VALUES ('A', 'Cajero', 'sales.create,register.operate,customers.manage');
            INSERT INTO Roles VALUES ('B', 'Encargado', 'settings.manage,reports.view,purchasing.manage,register.close');
            INSERT INTO Roles VALUES ('C', 'Vendedor', 'sales.create');
            """);

        await ExecAsync(c, Toki.Identity.Persistence.Migrations.SegmentedPermissionsIdentity.Expand("Roles"));

        (await PermissionsAsync(c, "A")).ShouldBe(["sales.create", "register.operate", "customers.manage", "register.open", "register.close", "register.cash-in", "credit.collect"]);
        (await PermissionsAsync(c, "B")).ShouldBe(["settings.manage", "reports.view", "purchasing.manage", "register.close",
            "registers.manage", "backups.manage", "audit.view", "shifts.view", "fiscal.reports", "payables.pay"]);
        (await PermissionsAsync(c, "C")).ShouldBe(["sales.create"]);
        (await PermissionsAsync(c, Admin.ToUpperInvariant())).ShouldBe(["users.manage"]); // Administrador always has everything anyway
    }

    /// <summary>The whole migration on a store with users and an edited role (SQL Server CI job, or TOKI_TEST_DB=sqlserver locally).</summary>
    [Fact]
    public async Task Upgrading_a_store_keeps_every_role_s_abilities_and_adds_the_templates()
    {
        if (!TestSqlServer.IsEnabled) Assert.Skip("Sqlite creates the tables from the model; migrations only run on SQL Server (TOKI_TEST_DB=sqlserver).");
        await using var db = NewDatabase();
        try
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261015220045_CostsViewIdentity");
            await db.Database.ExecuteSqlRawAsync($"""
                INSERT INTO [identity].[Users] (Id, Name, Code, PinHash, RoleId, IsActive, FailedAttempts) VALUES (NEWID(), N'Ana', 'ANA', 'x', '{Cashier}', 1, 0);
                INSERT INTO [identity].[Roles] (Id, Name, Permissions) VALUES ('11111111-1111-1111-1111-111111111111', N'Encargado', 'sales.create,register.operate,settings.manage,reports.view,purchasing.manage');
                INSERT INTO [identity].[Roles] (Id, Name, Permissions) VALUES ('22222222-2222-2222-2222-222222222222', N'Contador', 'reports.view');
                """);

            await migrator.MigrateAsync();

            var roles = await db.Roles.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Permissions);
            roles[new Guid(Cashier)].ShouldBe(["sales.create", "inventory.view", "customers.manage", "register.open", "register.close", "register.cash-in", "credit.collect"], ignoreOrder: true);
            roles[new Guid("11111111-1111-1111-1111-111111111111")].ShouldBe(["sales.create", "settings.manage", "reports.view", "purchasing.manage",
                "register.open", "register.close", "register.cash-in", "credit.collect", "registers.manage", "backups.manage", "audit.view", "shifts.view", "fiscal.reports", "payables.pay"], ignoreOrder: true);
            roles[new Guid("0199e000-0000-7000-8000-000000000002")].ShouldContain("register.open");
            roles[new Guid("0199e000-0000-7000-8000-000000000002")].ShouldContain("shifts.view");
            roles[new Guid(Admin)].ShouldContain("payables.pay");
            roles.ShouldContainKey(new Guid("0199e000-0000-7000-8000-000000000004"));
            roles.ShouldContainKey(new Guid("0199e000-0000-7000-8000-000000000005"));
            roles.ShouldNotContainKey(new Guid("0199e000-0000-7000-8000-000000000006")); // the store already had a "Contador"
            roles.Values.ShouldAllBe(p => !p.Contains("register.operate"));
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task A_new_install_gets_the_new_Cajero_that_does_not_open_the_register()
    {
        if (!TestSqlServer.IsEnabled) Assert.Skip("Sqlite creates the tables from the model; migrations only run on SQL Server (TOKI_TEST_DB=sqlserver).");
        await using var db = NewDatabase();
        try
        {
            await db.Database.MigrateAsync();
            var roles = await db.Roles.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Permissions);
            roles[new Guid(Cashier)].ShouldBe(["sales.create", "register.close", "register.cash-in", "credit.collect", "inventory.view", "customers.manage"]);
            roles.Count.ShouldBe(6);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    private static IdentityDbContext NewDatabase() => new(new DbContextOptionsBuilder<IdentityDbContext>().UseSqlServer(
        new SqlConnectionStringBuilder(TestSqlServer.BaseConnectionString) { InitialCatalog = $"toki_test_{Guid.NewGuid():N}" }.ConnectionString).Options);

    private static async Task ExecAsync(SqliteConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string[]> PermissionsAsync(SqliteConnection c, string id)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Permissions FROM Roles WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return ((string)(await cmd.ExecuteScalarAsync())!).Split(',');
    }
}

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.BuildingBlocks;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.Inventory.Domain;
using Abastalia.Inventory.Persistence;
using Abastalia.IntegrationTests.Infra;

namespace Abastalia.IntegrationTests.Platform;

/// <summary>TK-319 (ADR-0013): two businesses in one database (the future Lite one). The EF filter "Tenant" hides the other's rows; on SQL Server,
/// Row-Level Security hides them too with the filters off, and refuses writing a row for another business. Each business can repeat a
/// barcode (its key leads with TenantId).</summary>
public sealed class TenantIsolationTests
{
    private static readonly Guid A = Guid.CreateVersion7(), B = Guid.CreateVersion7();

    [Fact]
    public async Task The_EF_filter_shows_each_business_only_its_rows()
    {
        var file = Path.Combine(Path.GetTempPath(), $"abastalia_test_{Guid.NewGuid():N}.db");
        try
        {
            var (services, tenant) = await TwoBusinessesAsync("Sqlite", $"Data Source={file};Pooling=False");
            tenant.TenantId = A;
            (await NamesAsync(services, filtered: true)).ShouldBe(["Arroz de A"]);
            (await NamesAsync(services, filtered: false)).ShouldBe(["Arroz de A", "Arroz de B"], ignoreOrder: true); // the filter did the hiding
            tenant.TenantId = B;
            (await NamesAsync(services, filtered: true)).ShouldBe(["Arroz de B"]);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Row_level_security_hides_the_other_business_with_the_EF_filters_off()
    {
        if (!TestSqlServer.IsEnabled) Assert.Skip("Row-Level Security is SQL Server's (ABASTALIA_TEST_DB=sqlserver).");
        var connection = new SqlConnectionStringBuilder(TestSqlServer.BaseConnectionString) { InitialCatalog = $"abastalia_test_{Guid.NewGuid():N}" }.ConnectionString;
        var (services, tenant) = await TwoBusinessesAsync("SqlServer", connection);
        try
        {
            tenant.TenantId = A;
            (await NamesAsync(services, filtered: false)).ShouldBe(["Arroz de A"]);
            await using (var scope = services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
                (await db.Database.ExecuteSqlRawAsync("UPDATE inventory.Products SET Price = 1")).ShouldBe(1); // hand-written SQL too
                var other = $"INSERT INTO inventory.Barcodes (TenantId, Code, ProductId) VALUES ('{B}', '7460000000024', NEWID())";
                (await Should.ThrowAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(other))).Message.ShouldContain("block predicate");
            }
            tenant.TenantId = B;
            (await NamesAsync(services, filtered: false)).ShouldBe(["Arroz de B"]);
        }
        finally
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<InventoryDbContext>().Database.EnsureDeletedAsync();
        }
    }

    /// <summary>A Lite-like container (MultiTenant on), its database created, and a product with barcode 7460000000017 written by each business.</summary>
    private static async Task<(ServiceProvider, TestTenant)> TwoBusinessesAsync(string provider, string connectionString)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = provider,
            ["Database:ConnectionString"] = connectionString,
            ["Database:MultiTenant"] = "true",
        }).Build();
        var tenant = new TestTenant();
        var services = new ServiceCollection().AddSingleton<ITenantContext>(tenant).AddAbastalia(config, []).AddModuleDbContext<InventoryDbContext>().BuildServiceProvider();
        await AbastaliaServices.CreateDatabaseAsync(services);
        foreach (var (id, name) in new[] { (A, "Arroz de A"), (B, "Arroz de B") })
        {
            tenant.TenantId = id;
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            var product = new Product { Id = Guid.CreateVersion7(), Name = name, Price = 50 };
            db.Products.Add(product);
            db.Barcodes.Add(new Barcode { Code = "7460000000017", ProductId = product.Id });
            await db.SaveChangesAsync();
        }
        return (services, tenant);
    }

    private static async Task<List<string>> NamesAsync(IServiceProvider services, bool filtered)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        return await (filtered ? db.Products : db.Products.IgnoreQueryFilters()).Select(p => p.Name).ToListAsync();
    }

    private sealed class TestTenant : ITenantContext
    {
        public Guid TenantId { get; set; }
    }
}

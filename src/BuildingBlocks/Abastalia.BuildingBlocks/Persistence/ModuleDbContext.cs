using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;

namespace Abastalia.BuildingBlocks.Persistence;

/// <summary>Base for every module's DbContext: one schema per module, money as decimal(19,4), dates in UTC.
/// Each assembly holds exactly one context, so it applies every configuration in its assembly.</summary>
public abstract class ModuleDbContext(DbContextOptions options) : DbContext(options)
{
    /// <summary>Text columns that hold JSON or long notes (maps to nvarchar(max) on SQL Server).</summary>
    public const int LongText = 8000;

    /// <summary>TK-319: the column (a shadow property, or a CLR one on seeded types) that says which business owns a row.</summary>
    public const string TenantColumn = "TenantId";

    /// <summary>The module's schema. Sqlite ignores schemas, so table names must be unique across modules.</summary>
    protected abstract string Schema { get; }

    private Guid? _tenantId;

    /// <summary>The business this context reads and writes. Contexts built outside DI (design time, tests) are the store's.</summary>
    public Guid TenantId => _tenantId ??= this.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider
        ?.GetService<ITenantContext>()?.TenantId ?? StoreTenant.Id;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(GetType().Assembly);
        foreach (var entity in modelBuilder.Model.GetEntityTypes().Where(e => !typeof(ISharedAcrossTenants).IsAssignableFrom(e.ClrType)).ToList())
            AddTenant(modelBuilder, entity);
    }

    /// <summary>TK-319 (ADR-0013): every row carries its business; keys and unique indexes that two businesses could repeat lead with it
    /// (a Guid never repeats, so keys and indexes made only of Guids stay as they are); the named filter "Tenant" reads only this context's
    /// business. In the store the tenant is always <see cref="StoreTenant.Id"/>, so the filter is trivially true.</summary>
    private void AddTenant(ModelBuilder modelBuilder, IMutableEntityType entity)
    {
        var tenant = entity.FindProperty(TenantColumn) ?? entity.AddProperty(TenantColumn, typeof(Guid));
        var builder = modelBuilder.Entity(entity.Name);

        var key = entity.FindPrimaryKey()!;
        var identity = key.Properties is [{ ValueGenerated: ValueGenerated.OnAdd } p] && (p.ClrType == typeof(int) || p.ClrType == typeof(long));
        if (!identity && (!AllGuids(key.Properties) || entity.GetSeedData().Any()))
            builder.HasKey([TenantColumn, .. key.Properties.Select(p => p.Name)]);

        foreach (var index in entity.GetIndexes().Where(i => i.IsUnique && !AllGuids(i.Properties)).ToList())
        {
            var (properties, name, filter) = (index.Properties.ToList(), (string?)index[RelationalAnnotationNames.Name], index.GetFilter());
            entity.RemoveIndex(index);
            var replaced = entity.AddIndex([tenant, .. properties]);
            replaced.IsUnique = true;
            if (name is not null) replaced.SetDatabaseName(name);
            replaced.SetFilter(filter);
        }

        var row = Expression.Parameter(entity.ClrType, "e");
        builder.HasQueryFilter("Tenant", Expression.Lambda(Expression.Equal(
            Expression.Call(typeof(EF), nameof(EF.Property), [typeof(Guid)], row, Expression.Constant(TenantColumn)),
            Expression.Property(Expression.Constant(this), nameof(TenantId))), row));

        static bool AllGuids(IEnumerable<IMutableProperty> properties) => properties.All(p => p.ClrType == typeof(Guid));
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTenant();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampTenant();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>A new row always belongs to the context's business, whatever the handler set (it never comes from the request, §10).</summary>
    private void StampTenant()
    {
        foreach (var e in ChangeTracker.Entries())
            if (e.State == EntityState.Added && e.Metadata.FindProperty(TenantColumn) is { } p) e.Property(p.Name).CurrentValue = TenantId;
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(19, 4);
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcConverter>();
        configurationBuilder.Properties<string>().HaveMaxLength(200);
    }

    // Unspecified means UTC (it comes from our own UTC math or a client without "Z"); only Local is converted.
    private sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Toki.BuildingBlocks.Persistence;

/// <summary>Base for every module's DbContext: one schema per module, money as decimal(19,4), dates in UTC.
/// Each assembly holds exactly one context, so it applies every configuration in its assembly.</summary>
public abstract class ModuleDbContext(DbContextOptions options) : DbContext(options)
{
    /// <summary>Text columns that hold JSON or long notes (maps to nvarchar(max) on SQL Server).</summary>
    public const int TextoLargo = 8000;

    /// <summary>The module's schema. Sqlite ignores schemas, so table names must be unique across modules.</summary>
    protected abstract string Schema { get; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(GetType().Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(19, 4);
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcConverter>();
        configurationBuilder.Properties<string>().HaveMaxLength(200);
    }

    private sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}

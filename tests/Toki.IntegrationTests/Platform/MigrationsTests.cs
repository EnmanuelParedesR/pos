using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Shouldly;
using Toki.BuildingBlocks.Persistence;

namespace Toki.IntegrationTests.Platform;

/// <summary>TK-090 guards: a model change without its migration fails here, not in a store's database;
/// and no later migration rewrites data the owner edits (R6 #9).</summary>
public sealed class MigrationsTests
{
    private static readonly Guid AdministratorId = new("0199e000-0000-7000-8000-000000000001");

    public static TheoryData<string> Contexts() => [.. DesignTimeFactories().Select(ContextName)];

    [Theory]
    [MemberData(nameof(Contexts))]
    public void Every_model_change_has_its_migration(string context)
    {
        using var db = Create(context);
        db.Database.GetMigrations().ShouldNotBeEmpty();
        db.Database.HasPendingModelChanges().ShouldBeFalse($"{context}: a migration is missing. Run: dotnet dotnet-ef migrations add <Name> --project <module> --startup-project <module> --context {context} --output-dir Persistence/Migrations");
    }

    [Theory]
    [MemberData(nameof(Contexts))]
    public void New_migrations_do_not_rewrite_data_the_owner_edits(string context)
    {
        using var db = Create(context);
        var assembly = db.GetService<IMigrationsAssembly>();
        foreach (var (id, type) in assembly.Migrations.Where(m => !m.Key.Contains("_Initial", StringComparison.Ordinal)))
        {
            var touched = assembly.CreateMigration(type, db.Database.ProviderName!).UpOperations.Where(o => o switch
            {
                InsertDataOperation i => IsProtected(i.Table),
                UpdateDataOperation u => IsProtected(u.Table) && !(u.Table == "Roles" && Equals(u.KeyValues[0, 0], AdministratorId)),
                DeleteDataOperation d => IsProtected(d.Table),
                _ => false,
            });
            touched.ShouldBeEmpty($"{id} changes rows the owner edits (roles, business, label formats). Write that change with migrationBuilder.Sql, respecting what the store already changed.");
        }

        static bool IsProtected(string table) => table is "Roles" or "Business" or "LabelFormats";
    }

    /// <summary>Sqlite ignores column lengths, so an enum name longer than its column only failed on SQL Server.</summary>
    [Theory]
    [MemberData(nameof(Contexts))]
    public void Every_enum_stored_as_text_fits_its_column(string context)
    {
        using var db = Create(context);
        var tooShort = from e in db.Model.GetEntityTypes()
                       from p in e.GetProperties()
                       let type = Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType
                       where type.IsEnum && p.GetProviderClrType() == typeof(string) && p.GetMaxLength() is not null
                       let longest = Enum.GetNames(type).Max(n => n.Length)
                       where longest > p.GetMaxLength()
                       select $"{e.GetTableName()}.{p.Name}: {p.GetMaxLength()} < {longest}";
        tooShort.ShouldBeEmpty();
    }

    [Fact]
    public void Every_module_has_migrations() => DesignTimeFactories().Count().ShouldBe(9);

    private static DbContext Create(string context)
    {
        var factory = DesignTimeFactories().Single(f => ContextName(f) == context);
        return (DbContext)factory.GetMethod("CreateDbContext")!.Invoke(Activator.CreateInstance(factory, nonPublic: true), [Array.Empty<string>()])!;
    }

    private static string ContextName(Type factory) => factory.BaseType!.GetGenericArguments()[0].Name;

    private static IEnumerable<Type> DesignTimeFactories() =>
        AppDomain.CurrentDomain.GetAssemblies().Concat(typeof(Program).Assembly.GetReferencedAssemblies().Where(a => a.Name!.StartsWith("Toki.", StringComparison.Ordinal)).Select(System.Reflection.Assembly.Load))
            .Distinct().SelectMany(a => a.GetTypes())
            .Where(t => t is { IsAbstract: false, BaseType.IsGenericType: true } && t.BaseType.GetGenericTypeDefinition() == typeof(DesignTimeFactory<>))
            .DistinctBy(t => t.FullName);
}

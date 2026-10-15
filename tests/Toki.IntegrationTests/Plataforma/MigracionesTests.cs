using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Shouldly;
using Toki.BuildingBlocks.Persistence;

namespace Toki.IntegrationTests.Plataforma;

/// <summary>TK-090 guard: a model change without its migration fails here, not in a store's database.</summary>
public sealed class MigracionesTests
{
    public static TheoryData<string> Contextos() =>
        [.. FabricasDeDiseno().Select(f => f.GetInterfaces().Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDesignTimeDbContextFactory<>)).GetGenericArguments()[0].Name)];

    [Theory]
    [MemberData(nameof(Contextos))]
    public void Cada_cambio_del_modelo_tiene_su_migracion(string contexto)
    {
        var fabrica = FabricasDeDiseno().Single(f => f.BaseType!.GetGenericArguments()[0].Name == contexto);
        using var db = (DbContext)fabrica.GetMethod("CreateDbContext")!.Invoke(Activator.CreateInstance(fabrica, nonPublic: true), [Array.Empty<string>()])!;
        db.Database.GetMigrations().ShouldNotBeEmpty();
        db.Database.HasPendingModelChanges().ShouldBeFalse($"{contexto}: falta una migración. Ejecute: dotnet dotnet-ef migrations add <Nombre> --project <módulo> --startup-project <módulo> --context {contexto} --output-dir Persistence/Migraciones");
    }

    [Fact]
    public void Todos_los_modulos_tienen_migraciones() => FabricasDeDiseno().Count().ShouldBe(9);

    private static IEnumerable<Type> FabricasDeDiseno() =>
        AppDomain.CurrentDomain.GetAssemblies().Concat(typeof(Program).Assembly.GetReferencedAssemblies().Where(a => a.Name!.StartsWith("Toki.", StringComparison.Ordinal)).Select(System.Reflection.Assembly.Load))
            .Distinct().SelectMany(a => a.GetTypes())
            .Where(t => t is { IsAbstract: false, BaseType.IsGenericType: true } && t.BaseType.GetGenericTypeDefinition() == typeof(DisenoDbContext<>))
            .DistinctBy(t => t.FullName);
}

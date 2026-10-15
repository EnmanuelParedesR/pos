using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Shouldly;
using Toki.BuildingBlocks.Persistence;

namespace Toki.IntegrationTests.Plataforma;

/// <summary>TK-090 guards: a model change without its migration fails here, not in a store's database;
/// and no later migration rewrites data the owner edits (R6 #9).</summary>
public sealed class MigracionesTests
{
    private static readonly Guid AdministradorId = new("0199e000-0000-7000-8000-000000000001");

    public static TheoryData<string> Contextos() => [.. FabricasDeDiseno().Select(Contexto)];

    [Theory]
    [MemberData(nameof(Contextos))]
    public void Cada_cambio_del_modelo_tiene_su_migracion(string contexto)
    {
        using var db = Crear(contexto);
        db.Database.GetMigrations().ShouldNotBeEmpty();
        db.Database.HasPendingModelChanges().ShouldBeFalse($"{contexto}: falta una migración. Ejecute: dotnet dotnet-ef migrations add <Nombre> --project <módulo> --startup-project <módulo> --context {contexto} --output-dir Persistence/Migraciones");
    }

    [Theory]
    [MemberData(nameof(Contextos))]
    public void Las_migraciones_nuevas_no_reescriben_datos_que_edita_el_dueno(string contexto)
    {
        using var db = Crear(contexto);
        var ensamblado = db.GetService<IMigrationsAssembly>();
        foreach (var (id, tipo) in ensamblado.Migrations.Where(m => !m.Key.Contains("_Inicial", StringComparison.Ordinal)))
        {
            var tocadas = ensamblado.CreateMigration(tipo, db.Database.ProviderName!).UpOperations.Where(o => o switch
            {
                InsertDataOperation i => Protegida(i.Table),
                UpdateDataOperation u => Protegida(u.Table) && !(u.Table == "Roles" && Equals(u.KeyValues[0, 0], AdministradorId)),
                DeleteDataOperation d => Protegida(d.Table),
                _ => false,
            });
            tocadas.ShouldBeEmpty($"{id} cambia filas que el dueño edita (roles, negocio, formatos de etiqueta). Escriba ese cambio con migrationBuilder.Sql respetando lo que la tienda ya cambió.");
        }

        static bool Protegida(string tabla) => tabla is "Roles" or "Negocio" or "FormatosEtiqueta";
    }

    [Fact]
    public void Todos_los_modulos_tienen_migraciones() => FabricasDeDiseno().Count().ShouldBe(9);

    private static DbContext Crear(string contexto)
    {
        var fabrica = FabricasDeDiseno().Single(f => Contexto(f) == contexto);
        return (DbContext)fabrica.GetMethod("CreateDbContext")!.Invoke(Activator.CreateInstance(fabrica, nonPublic: true), [Array.Empty<string>()])!;
    }

    private static string Contexto(Type fabrica) => fabrica.BaseType!.GetGenericArguments()[0].Name;

    private static IEnumerable<Type> FabricasDeDiseno() =>
        AppDomain.CurrentDomain.GetAssemblies().Concat(typeof(Program).Assembly.GetReferencedAssemblies().Where(a => a.Name!.StartsWith("Toki.", StringComparison.Ordinal)).Select(System.Reflection.Assembly.Load))
            .Distinct().SelectMany(a => a.GetTypes())
            .Where(t => t is { IsAbstract: false, BaseType.IsGenericType: true } && t.BaseType.GetGenericTypeDefinition() == typeof(DisenoDbContext<>))
            .DistinctBy(t => t.FullName);
}

using Assembly = System.Reflection.Assembly;
using System.Text.RegularExpressions;
using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using Shouldly;
using Toki.BuildingBlocks.Messaging;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Toki.ArchitectureTests;

public sealed partial class ReglasDeArquitectura
{
    private static readonly Assembly[] Ensamblados = CargarToki();
    private static readonly Architecture Arquitectura = new ArchLoader().LoadAssemblies(Ensamblados).Build();
    private static readonly string Raiz = BuscarRaiz();

    private static Assembly[] CargarToki()
    {
        var vistos = new Dictionary<string, Assembly>();
        var pendientes = new Stack<Assembly>([typeof(Program).Assembly]);
        while (pendientes.TryPop(out var a))
        {
            if (!vistos.TryAdd(a.GetName().Name!, a)) continue;
            foreach (var r in a.GetReferencedAssemblies().Where(r => r.Name!.StartsWith("Toki.", StringComparison.Ordinal)))
                pendientes.Push(Assembly.Load(r));
        }
        return [.. vistos.Values];
    }

    private static IEnumerable<Assembly> Modulos => Ensamblados.Where(a =>
        a.GetName().Name is { } n && n != "Toki.Host.Local" && !n.StartsWith("Toki.BuildingBlocks", StringComparison.Ordinal)
        && n != "Toki.SharedKernel" && !n.EndsWith(".Contracts", StringComparison.Ordinal));

    [Fact]
    public void Un_modulo_solo_ve_los_Contracts_de_otro_modulo()
    {
        var nombresModulos = Modulos.Select(m => m.GetName().Name).ToHashSet();
        foreach (var modulo in Modulos)
        {
            var prohibidos = modulo.GetReferencedAssemblies().Select(r => r.Name).Where(nombresModulos.Contains).ToList();
            prohibidos.ShouldBeEmpty($"{modulo.GetName().Name} referencia internos de {string.Join(", ", prohibidos)}");
        }
    }

    [Fact]
    public void Los_handlers_son_internos_y_sellados() =>
        Classes().That().ImplementInterface(typeof(ICommandHandler<,>))
            .Should().NotBePublic().AndShould().BeSealed().WithoutRequiringPositiveResults()
            .Check(Arquitectura);

    [Fact]
    public void Los_Contracts_no_dependen_de_EF_ni_de_AspNetCore() =>
        Types().That().ResideInAssemblyMatching(@"Toki\..*\.Contracts")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"Microsoft\.(EntityFrameworkCore|AspNetCore).*"))
            .WithoutRequiringPositiveResults()
            .Check(Arquitectura);

    [Fact]
    public void El_dominio_no_depende_de_EF_ni_de_HTTP() =>
        Types().That().ResideInNamespaceMatching(@"Toki\..*\.Domain")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"Microsoft\.(EntityFrameworkCore|AspNetCore\.Http|AspNetCore\.Routing).*"))
            .WithoutRequiringPositiveResults()
            .Check(Arquitectura);

    /// <summary>planes.md §6: selling, issuing e-CF and their queue never depend on the license or on payment.</summary>
    [Fact]
    public void Vender_y_facturar_nunca_dependen_de_la_licencia() =>
        Types().That().ResideInAssemblyMatching(@"^Toki\.(Ventas|Fiscal|Caja),")
            .Should().NotDependOnAny(Types().That().Are(typeof(Toki.BuildingBlocks.Security.IFeatureService)))
            .Check(Arquitectura);

    [Fact]
    public void No_hay_procedimientos_almacenados_ni_triggers()
    {
        var culpables = Directory.EnumerateFiles(Path.Combine(Raiz, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => SqlProhibido().IsMatch(File.ReadAllText(f)))
            .ToList();
        culpables.ShouldBeEmpty();
    }

    [Fact]
    public void No_se_usan_paquetes_con_licencia_comercial()
    {
        var paquetes = File.ReadAllText(Path.Combine(Raiz, "Directory.Packages.props"));
        foreach (var prohibido in new[] { "\"MediatR\"", "\"AutoMapper\"", "\"MassTransit\"", "\"FluentAssertions\"", "\"Duende." })
            paquetes.ShouldNotContain(prohibido);
    }

    [GeneratedRegex(@"CREATE\s+(OR\s+ALTER\s+)?(PROCEDURE|PROC|TRIGGER|FUNCTION)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SqlProhibido();

    private static string BuscarRaiz()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Toki.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No se encontró Toki.slnx");
    }
}

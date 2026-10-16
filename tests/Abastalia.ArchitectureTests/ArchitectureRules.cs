using Assembly = System.Reflection.Assembly;
using System.Text.RegularExpressions;
using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using Shouldly;
using Abastalia.BuildingBlocks.Messaging;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Abastalia.ArchitectureTests;

public sealed partial class ArchitectureRules
{
    private static readonly Assembly[] Assemblies = LoadAbastalia();
    private static readonly Architecture Architecture = new ArchLoader().LoadAssemblies(Assemblies).Build();
    private static readonly string Root = FindRoot();

    private static Assembly[] LoadAbastalia()
    {
        var seen = new Dictionary<string, Assembly>();
        var pending = new Stack<Assembly>([typeof(Program).Assembly]);
        while (pending.TryPop(out var a))
        {
            if (!seen.TryAdd(a.GetName().Name!, a)) continue;
            foreach (var r in a.GetReferencedAssemblies().Where(r => r.Name!.StartsWith("Abastalia.", StringComparison.Ordinal)))
                pending.Push(Assembly.Load(r));
        }
        return [.. seen.Values];
    }

    private static IEnumerable<Assembly> Modules => Assemblies.Where(a =>
        a.GetName().Name is { } n && n != "Abastalia.Host.Local" && !n.StartsWith("Abastalia.BuildingBlocks", StringComparison.Ordinal)
        && n != "Abastalia.SharedKernel" && !n.EndsWith(".Contracts", StringComparison.Ordinal));

    [Fact]
    public void A_module_only_sees_the_Contracts_of_another_module()
    {
        var moduleNames = Modules.Select(m => m.GetName().Name).ToHashSet();
        foreach (var module in Modules)
        {
            var forbidden = module.GetReferencedAssemblies().Select(r => r.Name).Where(moduleNames.Contains).ToList();
            forbidden.ShouldBeEmpty($"{module.GetName().Name} references internals of {string.Join(", ", forbidden)}");
        }
    }

    [Fact]
    public void Handlers_are_internal_and_sealed() =>
        Classes().That().ImplementInterface(typeof(ICommandHandler<,>))
            .Should().NotBePublic().AndShould().BeSealed().WithoutRequiringPositiveResults()
            .Check(Architecture);

    [Fact]
    public void Contracts_do_not_depend_on_EF_or_AspNetCore() =>
        Types().That().ResideInAssemblyMatching(@"Abastalia\..*\.Contracts")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"Microsoft\.(EntityFrameworkCore|AspNetCore).*"))
            .WithoutRequiringPositiveResults()
            .Check(Architecture);

    [Fact]
    public void The_domain_does_not_depend_on_EF_or_HTTP() =>
        Types().That().ResideInNamespaceMatching(@"Abastalia\..*\.Domain")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"Microsoft\.(EntityFrameworkCore|AspNetCore\.Http|AspNetCore\.Routing).*"))
            .WithoutRequiringPositiveResults()
            .Check(Architecture);

    /// <summary>planes.md §6: selling, issuing e-CF and their queue never depend on the license or on payment.</summary>
    [Fact]
    public void Selling_and_invoicing_never_depend_on_the_license() =>
        Types().That().ResideInAssemblyMatching(@"^Abastalia\.(Sales|Fiscal|CashRegister),")
            .Should().NotDependOnAny(Types().That().Are(typeof(Abastalia.BuildingBlocks.Security.IFeatureService)))
            .Check(Architecture);

    [Fact]
    public void There_are_no_stored_procedures_or_triggers()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => ForbiddenSql().IsMatch(File.ReadAllText(f)))
            .ToList();
        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void No_commercially_licensed_packages_are_used()
    {
        var packages = File.ReadAllText(Path.Combine(Root, "Directory.Packages.props"));
        foreach (var forbidden in new[] { "\"MediatR\"", "\"AutoMapper\"", "\"MassTransit\"", "\"FluentAssertions\"", "\"Duende." })
            packages.ShouldNotContain(forbidden);
    }

    [GeneratedRegex(@"CREATE\s+(OR\s+ALTER\s+)?(PROCEDURE|PROC|TRIGGER|FUNCTION)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenSql();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Abastalia.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Abastalia.slnx not found");
    }
}

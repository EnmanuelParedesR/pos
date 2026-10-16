using System.Globalization;
using System.Text.RegularExpressions;
using Shouldly;

namespace Toki.Client.UI.Tests;

/// <summary>TK-243: Escuadra's safety net for color (docs/sistema-de-diseno.md, "Reglas automáticas"). Every documented text/background
/// pair meets WCAG AA (4.5:1 text, 3:1 borders and focus) in the register's theme and in the back office's light and dark themes;
/// and no CSS rule writes a hex color by hand: colors live only in the token definitions (--name: #hex).</summary>
public sealed partial class EscuadraTokenTests
{
    private static readonly string Root = FindRoot();
    private static readonly string Register = Path.Combine(Root, "src", "Client", "Toki.Escuadra", "wwwroot", "escuadra.css");
    private static readonly string Admin = Path.Combine(Root, "src", "Client", "Toki.Escuadra", "wwwroot", "escuadra-admin.css");
    private static readonly string Client = Path.Combine(Root, "src", "Client", "Toki.Client.UI", "wwwroot", "client.css");

    // (text, background, minimum)
    private static readonly (string, string, double)[] RegisterPairs =
    [
        ("text", "background", 4.5), ("text", "surface", 4.5), ("text", "surface-2", 4.5), ("text", "brand-tint", 4.5),
        ("text-2", "background", 4.5), ("text-2", "surface", 4.5), ("text-2", "surface-2", 4.5),
        ("brand", "surface", 4.5), ("brand", "background", 4.5), ("brand", "brand-tint", 4.5), ("paper", "brand", 4.5), ("paper", "brand-hover", 4.5),
        ("success", "success-bg", 4.5), ("warning", "warning-bg", 4.5), ("error", "error-bg", 4.5), ("error", "surface", 4.5),
        ("frame-text", "frame", 4.5), ("frame-muted", "frame", 4.5), ("frame-text", "frame-hover", 4.5), ("offline", "offline-bg", 4.5),
        ("control-border", "surface", 3), ("control-border", "background", 3), ("brand", "background", 3),
    ];

    private static readonly (string, string, double)[] AdminPairs =
    [
        ("text", "background", 4.5), ("text", "surface", 4.5), ("text-2", "background", 4.5), ("text-2", "surface", 4.5), ("text-2", "surface-2", 4.5),
        ("text-3", "background", 4.5), ("text-3", "surface", 4.5), ("brand", "surface", 4.5), ("paper", "brand-600", 4.5), ("brand-700", "brand-50", 4.5),
        ("success", "success-bg", 4.5), ("warning", "warning-bg", 4.5), ("error", "error-bg", 4.5), ("tip-text", "tip-bg", 4.5), ("tip-muted", "tip-bg", 4.5),
    ];

    [Fact]
    public void The_register_theme_meets_AA() => AssertPairs("caja", Tokens(Register, s => s == ":root"), RegisterPairs);

    [Fact]
    public void The_back_office_meets_AA_in_light_and_dark()
    {
        var root = Tokens(Register, s => s == ":root");
        var light = Merge(root, Tokens(Admin, s => s == ".esc-admin"));
        AssertPairs("administración clara", light, AdminPairs);
        AssertPairs("administración oscura", Merge(light, Tokens(Admin, s => s.Contains("data-theme=\"dark\"] .esc-admin", StringComparison.Ordinal))), AdminPairs);
    }

    [Fact]
    public void No_rule_writes_a_hex_color_outside_the_tokens()
    {
        var found = new List<string>();
        foreach (var file in new[] { Register, Admin, Client })
            foreach (var (selector, body) in Blocks(file))
                foreach (var declaration in body.Split(';'))
                    if (!declaration.Trim().StartsWith("--", StringComparison.Ordinal) && Hex().IsMatch(declaration))
                        found.Add($"{Path.GetFileName(file)}: {selector.Trim()} {{ {declaration.Trim()} }}");
        // Razor components: inline styles too (the logo's artwork is the brand itself, fixed by design).
        foreach (var file in Directory.GetFiles(Path.Combine(Root, "src", "Client", "Toki.Escuadra", "Components"), "*.razor"))
            if (Path.GetFileName(file) != "Logo.razor" && Hex().IsMatch(File.ReadAllText(file)))
                found.Add(Path.GetFileName(file));
        found.ShouldBeEmpty();
    }

    [Fact]
    public void The_contrast_check_catches_a_bad_pair() => Contrast("#7A838C", "#FFFFFF").ShouldBeLessThan(4.5);

    private static void AssertPairs(string theme, Dictionary<string, string> tokens, (string, string, double)[] pairs)
    {
        var failing = pairs
            .Select(p => (p.Item1, p.Item2, p.Item3, Ratio: Contrast(Resolve(tokens, p.Item1), Resolve(tokens, p.Item2))))
            .Where(p => p.Ratio < p.Item3)
            .Select(p => $"{theme}: --{p.Item1} sobre --{p.Item2} = {p.Ratio:0.00}:1 (mínimo {p.Item3}:1)");
        failing.ShouldBeEmpty();
    }

    private static string Resolve(Dictionary<string, string> tokens, string name)
    {
        var value = tokens.GetValueOrDefault(name) ?? throw new ShouldAssertException($"--{name} is not defined");
        var reference = Var().Match(value);
        return reference.Success ? Resolve(tokens, reference.Groups[1].Value) : value;
    }

    private static Dictionary<string, string> Merge(Dictionary<string, string> under, Dictionary<string, string> over)
    {
        var all = new Dictionary<string, string>(under);
        foreach (var (k, v) in over) all[k] = v;
        return all;
    }

    private static Dictionary<string, string> Tokens(string file, Func<string, bool> selector)
    {
        var tokens = new Dictionary<string, string>();
        foreach (var (s, body) in Blocks(file))
            if (selector(s.Trim()))
                foreach (Match m in Token().Matches(body))
                    tokens[m.Groups[1].Value] = m.Groups[2].Value.Trim();
        return tokens;
    }

    /// <summary>Each innermost rule (a rule inside @media comes out on its own), comments removed.</summary>
    private static IEnumerable<(string Selector, string Body)> Blocks(string file) =>
        Rule().Matches(Comment().Replace(File.ReadAllText(file), "")).Select(m => (m.Groups[1].Value, m.Groups[2].Value));

    private static double Contrast(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        hex.ShouldMatch("^#[0-9A-Fa-f]{6}$");
        double Channel(int i)
        {
            var c = int.Parse(hex.AsSpan(1 + i * 2, 2), NumberStyles.HexNumber) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(0) + 0.7152 * Channel(1) + 0.0722 * Channel(2);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Toki.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Toki.slnx not found");
    }

    [GeneratedRegex(@"([^{}]+)\{([^{}]*)\}")] private static partial Regex Rule();
    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)] private static partial Regex Comment();
    [GeneratedRegex(@"--([\w-]+)\s*:\s*([^;]+)")] private static partial Regex Token();
    [GeneratedRegex(@"var\(--([\w-]+)\)")] private static partial Regex Var();
    [GeneratedRegex(@"#[0-9A-Fa-f]{3,8}\b")] private static partial Regex Hex();
}

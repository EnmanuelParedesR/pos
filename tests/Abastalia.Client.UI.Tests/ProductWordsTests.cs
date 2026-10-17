using System.Text.RegularExpressions;
using Shouldly;

namespace Abastalia.Client.UI.Tests;

/// <summary>TK-353: the product never says "fiado" (the site's words since TK-312: "a crédito", "cuentas por cobrar", "abonos de crédito").
/// Scans what a person sees: the register and back office (Razor and C# strings), the modules' messages, receipts and reports, and
/// Talia's knowledge. Comment lines are skipped, code identifiers are English (OnAccount) so they never match.</summary>
public sealed partial class ProductWordsTests
{
    [Fact]
    public void No_screen_receipt_or_message_says_fiado()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Abastalia.slnx"))) root = root.Parent!;
        var files = new[] { "src/Client", "src/Modules" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root.FullName, d), "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Concat(Directory.EnumerateFiles(Path.Combine(root.FullName, "src", "Hosts", "Abastalia.Host.Cloud", "Assistant", "Knowledge"), "*.md"));
        var found = (from f in files
                     from line in File.ReadLines(f).Select((text, i) => (text, n: i + 1))
                     where !line.text.TrimStart().StartsWith("//", StringComparison.Ordinal) && Fiado().IsMatch(line.text)
                     select $"{Path.GetRelativePath(root.FullName, f)}:{line.n}").ToList();
        found.ShouldBeEmpty();
    }

    [GeneratedRegex(@"\bfiad[oa]s?\b|\bfiar(le|les)?\b|\bfía\b|\bfiao\b", RegexOptions.IgnoreCase)] private static partial Regex Fiado();
}

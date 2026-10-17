using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Abastalia.Host.Cloud.Assistant;

namespace Abastalia.Cloud.Tests.Talia;

/// <summary>TK-347: the knowledge base compiles, stays inside its rules and finds the right article for the questions people ask. Free and
/// deterministic, so it runs in CI; the model's side is in <see cref="TaliaLiveEvals"/>.</summary>
public sealed class TaliaKnowledgeTests
{
    private static readonly TaliaKnowledge Knowledge = TaliaKnowledge.Embedded;

    [Fact]
    public void The_compiled_base_meets_every_rule_Talia_checks_before_using_one()
    {
        // TK-350: the refresher runs the same Problems() on a base from Blob Storage before it replaces this one.
        Knowledge.Articles.Count.ShouldBeGreaterThanOrEqualTo(20);
        Knowledge.Problems().ShouldBeEmpty();
    }

    [Fact]
    public void Problems_catch_what_must_never_reach_Talia()
    {
        static string Article(string id, string audience, string body, string title = "Un título") =>
            $"---\nid: {id}\ntitle: {title}\naudience: {audience}\nlinks: [/precios]\n---\n\n## Una pregunta\n{body}\n";
        const string ok = "Abastalia vende y factura en la caja con o sin internet, y envía cada comprobante a la DGII cuando vuelve la conexión.";
        var bad = TaliaKnowledge.Parse([
            ("a", Article("a", "public", ok + " Muchos clientes compran fiado.")),
            ("b", Article("b", "public", ok + " Vea /app/billing o /inventario.")),
            ("c", Article("c", "panel", ok + " Corre en Azure con gpt-5-nano.", title: "Un título")),
            ("d", Article("d", "panel", "Muy corto.")),
        ]).Problems();

        bad.ShouldContain(p => p.StartsWith("a ·", StringComparison.Ordinal) && p.Contains("fiado"));
        bad.ShouldContain(p => p.StartsWith("b ·", StringComparison.Ordinal) && p.Contains("/app/billing"));
        bad.ShouldContain(p => p.StartsWith("b ·", StringComparison.Ordinal) && p.Contains("/inventario"));
        bad.ShouldContain(p => p.StartsWith("c ·", StringComparison.Ordinal) && p.Contains("azure"));
        bad.ShouldContain(p => p.StartsWith("d ·", StringComparison.Ordinal) && p.Contains("palabras"));
        bad.ShouldContain("título repetido: Un título");
        // A panel article may name panel screens and the word the panel shows.
        TaliaKnowledge.Parse([("e", Article("e", "panel", ok + " En /app/billing ve quién le debe (fiado)."))]).Problems().ShouldBeEmpty();
    }

    [Fact]
    public void The_prices_article_says_the_prices_the_site_shows()
    {
        var config = File.ReadAllText(Path.Combine(Repo.Root, "src", "Cloud", "panel", "src", "pages", "site", "config.ts"));
        var block = Regex.Match(config, @"monthlyUsd:\s*\{(?<body>[^}]*)\}").Groups["body"].Value;
        var prices = Regex.Matches(block, @"(?<code>\w+):\s*(?<usd>\d+)").Select(m => int.Parse(m.Groups["usd"].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        prices.Count.ShouldBeGreaterThan(5);
        var text = string.Join("\n", Knowledge.Articles.Single(a => a.Id == "precios").Sections.Select(s => s.Text));
        foreach (var usd in prices.Distinct()) text.ShouldContain($"US${usd}");
    }

    [Fact]
    public void Retrieval_finds_the_right_article_in_the_top_three_for_nine_in_ten_questions()
    {
        var cases = JsonSerializer.Deserialize<List<RetrievalCase>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Talia", "knowledge-evals.json")),
            WebJson)!;
        var inScope = cases.Where(c => c.Expect.Count > 0).ToList();
        inScope.Count.ShouldBeGreaterThanOrEqualTo(40);
        foreach (var id in cases.SelectMany(c => c.Expect)) Knowledge.Articles.ShouldContain(a => a.Id == id, $"evals name '{id}'");

        var misses = inScope.Where(c => !Knowledge.Search(c.Q, c.Audience == TaliaKnowledge.Panel, take: 3).Any(h => c.Expect.Contains(h.Article)))
            .Select(c => $"{c.Q} → {string.Join(", ", Knowledge.Search(c.Q, c.Audience == TaliaKnowledge.Panel, 3).Select(h => h.Article))} (quería {c.Expect[0]})")
            .ToList();
        var recall = 1 - (double)misses.Count / inScope.Count;
        var report = Path.Combine(Repo.Root, "TestResults", "talia");
        Directory.CreateDirectory(report);
        var summary = $"recall@3 = {recall:P1} ({inScope.Count - misses.Count}/{inScope.Count})\n" + string.Join("\n", misses);
        File.WriteAllText(Path.Combine(report, "retrieval.txt"), summary); // read by docs/talia.md's numbers; TestResults is not committed
        TestContext.Current.TestOutputHelper?.WriteLine(summary);
        recall.ShouldBeGreaterThanOrEqualTo(0.9, string.Join("\n", misses));
    }

    [Fact]
    public void The_site_never_finds_panel_only_articles()
    {
        var panelOnly = Knowledge.Articles.Where(a => a.Audience == TaliaKnowledge.Panel).ToList();
        panelOnly.ShouldNotBeEmpty();
        foreach (var article in panelOnly)
            foreach (var section in article.Sections)
                Knowledge.Search($"{article.Title} {section.Heading}", includePanel: false, take: 10).ShouldAllBe(h => h.Article != article.Id);
    }

    [Theory]
    [InlineData("¿Cuánto cuesta el plan Pro?", "preci")]
    [InlineData("factura electrónica e-CF", "ecf")]
    [InlineData("Las CAJERAS", "cajer")]
    [InlineData("el cajero", "cajer")]
    public void Tokens_fold_accents_case_plural_gender_and_synonyms(string text, string expected) =>
        TaliaKnowledge.Tokens(text).ShouldContain(expected);

    [Fact]
    public void Nonsense_finds_nothing() => Knowledge.Search("zxqv blorp", includePanel: true).ShouldBeEmpty();

    [Theory]
    [InlineData("**Plan Pro**: US$35", "Plan Pro: US$35")]
    [InlineData("## Precios\nEl plan", "Precios\nEl plan")]
    [InlineData("* Uno\n* Dos", "- Uno\n- Dos")]
    [InlineData("Vea [Precios](/precios).", "Vea Precios (/precios).")]
    [InlineData("Vea [/precios](/precios).", "Vea /precios.")]
    [InlineData("  ", "")]
    public void Clean_takes_markdown_out_of_answers(string raw, string clean) => TaliaLoop.Clean(raw).ShouldBe(clean);

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private sealed record RetrievalCase(string Q, string Audience, List<string> Expect);
}

internal static class Repo
{
    /// <summary>The repository root: the folder above the tests with Abastalia.slnx.</summary>
    public static string Root { get; } = Find();

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Abastalia.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("Abastalia.slnx");
    }
}

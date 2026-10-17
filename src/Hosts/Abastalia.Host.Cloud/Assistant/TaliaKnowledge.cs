using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Abastalia.Host.Cloud.Assistant;

/// <param name="Audience">public: the site and the panel; panel: only signed-in members.</param>
/// <param name="Links">Paths the article points to (/precios, /app/billing…), given to the model as is.</param>
/// <param name="Watch">TK-350: outside pages the article depends on (the DGII's); <see cref="TaliaSourceWatcher"/> tells staff when one changes.</param>
public sealed record KnowledgeArticle(string Id, string Title, string Audience, IReadOnlyList<string> Links, IReadOnlyList<KnowledgeSection> Sections,
    IReadOnlyList<string>? Watch = null);

public sealed record KnowledgeSection(string ArticleId, string Heading, string Text);

/// <summary>One section the search found, as the model gets it.</summary>
public sealed record KnowledgeHit(string Article, string Title, string Section, string Text, IReadOnlyList<string> Links, double Score);

/// <summary>TK-347: what Talia knows about Abastalia, from the Markdown files in <c>Assistant/Knowledge</c> (compiled in, one topic per file, one
/// answer per <c>##</c> section). Search is BM25 over the sections with light Spanish stemming: the whole base is a few hundred sections, so an
/// index in memory beats embeddings on cost and needs no service (docs/talia.md has the plan for when it grows).</summary>
public sealed partial class TaliaKnowledge
{
    public const string Public = "public";
    public const string Panel = "panel";
    private const string ResourcePrefix = "Abastalia.Talia.Knowledge.";
    private const double K1 = 1.2, B = 0.75;

    /// <summary>A hit below this score is noise (a shared common word); measured with the knowledge evals.</summary>
    public const double MinScore = 3.0;

    private readonly List<(KnowledgeSection Section, KnowledgeArticle Article, Dictionary<string, int> Terms, int Length)> _docs = [];
    private readonly Dictionary<string, int> _documentFrequency = [];
    private readonly double _averageLength;

    public IReadOnlyList<KnowledgeArticle> Articles { get; }

    /// <summary>First 12 hex of the SHA-256 of every file: logged with each answer, so a bad answer can be traced to the base it read.</summary>
    public string Version { get; }

    private static readonly Lazy<TaliaKnowledge> EmbeddedBase = new(() => Load(typeof(TaliaKnowledge).Assembly));

    public static TaliaKnowledge Embedded => EmbeddedBase.Value;

    private TaliaKnowledge(IReadOnlyList<KnowledgeArticle> articles, string version)
    {
        Articles = articles;
        Version = version;
        foreach (var article in articles)
            foreach (var section in article.Sections)
            {
                // The title and the heading say what the section answers: they count twice.
                var tokens = Tokens($"{article.Title} {section.Heading} {article.Title} {section.Heading} {section.Text}");
                var terms = tokens.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
                foreach (var term in terms.Keys) _documentFrequency[term] = _documentFrequency.GetValueOrDefault(term) + 1;
                _docs.Add((section, article, terms, tokens.Count));
            }
        _averageLength = _docs.Count == 0 ? 1 : _docs.Average(d => d.Length);
    }

    /// <summary>The same base with <paramref name="article"/> in place of the one with its id (TK-350: live prices replace the provisional ones).</summary>
    public TaliaKnowledge With(KnowledgeArticle article, string versionSuffix) =>
        new([.. Articles.Select(a => a.Id == article.Id ? article : a)], $"{Version}+{versionSuffix}");

    /// <summary>TK-350: what a base must meet before Talia uses it (the knowledge tests and the refresher both ask): unique ids and titles,
    /// sections of answer size, only pages that exist, nothing internal, and the site's words (never "fiado" in public, TK-312). Empty = valid.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (Articles.Count == 0) problems.Add("la base está vacía");
        problems.AddRange(Articles.GroupBy(a => a.Id).Where(g => g.Count() > 1).Select(g => $"id repetido: {g.Key}"));
        problems.AddRange(Articles.GroupBy(a => a.Title).Where(g => g.Count() > 1).Select(g => $"título repetido: {g.Key}"));
        foreach (var article in Articles)
        {
            problems.AddRange(article.Links.Where(l => !KnownPath().IsMatch(l)).Select(l => $"{article.Id}: enlace desconocido {l}"));
            foreach (var section in article.Sections)
            {
                var where = $"{article.Id} · {section.Heading}";
                var words = section.Text.Split((char[])[' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;
                if (words is < 12 or > 260) problems.Add($"{where}: {words} palabras (12 a 260)");
                foreach (Match path in TextPath().Matches(section.Text))
                {
                    var p = path.Value.TrimEnd('.', '/');
                    if (!KnownPath().IsMatch(p)) problems.Add($"{where}: ruta desconocida {p}");
                    else if (article.Audience == Public && p.StartsWith("/app/", StringComparison.Ordinal)) problems.Add($"{where}: un artículo público manda a {p}");
                }
                problems.AddRange(Internal.Where(w => section.Text.Contains(w, StringComparison.OrdinalIgnoreCase)).Select(w => $"{where}: dice '{w}'"));
                if (article.Audience == Public && Fiado().IsMatch($"{article.Title} {section.Heading} {section.Text}")) problems.Add($"{where}: dice fiado (el sitio dice crédito)");
            }
        }
        return problems;
    }

    /// <summary>Words that would mean internal detail leaked into the base.</summary>
    private static readonly string[] Internal = ["azure", "key vault", "openai", "gpt-", "sqlite", "kubernetes", "margen", "contraseña del", "TK-", "legacy"];

    /// <summary>The pages the chat turns into links (AssistantPage.tsx, <c>pathPattern</c>): keep both lists the same.</summary>
    [GeneratedRegex(@"^/(app(/[a-z-]+)*|precios|e-cf|guia-e-cf|comparar|glosario|certificacion|descargar|novedades|terminos|privacidad|carniceria|colmado|deli)?(#[a-z-]+)?$")]
    private static partial Regex KnownPath();

    [GeneratedRegex(@"(?<![\w.])/[a-z][a-z0-9/#-]*")]
    private static partial Regex TextPath();

    [GeneratedRegex(@"\bfia(do|da|dos|das|o|r)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Fiado();

    public static TaliaKnowledge Load(Assembly assembly)
    {
        var files = assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(n =>
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(n)!, Encoding.UTF8);
                return (Name: n[ResourcePrefix.Length..^3], Markdown: reader.ReadToEnd());
            });
        return Parse(files);
    }

    /// <summary>Parses the files (name without .md, content). Throws on a malformed file: the knowledge tests run every file through here.</summary>
    public static TaliaKnowledge Parse(IEnumerable<(string Name, string Markdown)> files)
    {
        var articles = new List<KnowledgeArticle>();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (name, markdown) in files)
        {
            var text = markdown.Replace("\r\n", "\n");
            hash.AppendData(Encoding.UTF8.GetBytes(name + "\n" + text));
            articles.Add(ParseArticle(name, text));
        }
        return new(articles, Convert.ToHexString(hash.GetHashAndReset())[..12].ToLowerInvariant());
    }

    private static KnowledgeArticle ParseArticle(string name, string text)
    {
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) throw new FormatException($"{name}: falta el encabezado ---");
        var end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0) throw new FormatException($"{name}: el encabezado no cierra con ---");
        var fields = text[4..end].Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(':', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1].Trim());
        string Field(string key) => fields.TryGetValue(key, out var v) && v.Length > 0 ? v : throw new FormatException($"{name}: falta '{key}'");
        var id = Field("id");
        if (id != name) throw new FormatException($"{name}: el id '{id}' no es el nombre del archivo");
        var audience = Field("audience");
        if (audience is not (Public or Panel)) throw new FormatException($"{name}: audience debe ser public o panel");
        var links = fields.TryGetValue("links", out var l) ? List(l) : [];

        var sections = new List<KnowledgeSection>();
        string? heading = null;
        var body = new StringBuilder();
        void Flush()
        {
            if (heading is not null && body.ToString().Trim() is { Length: > 0 } t) sections.Add(new(id, heading, t));
            body.Clear();
        }
        foreach (var line in text[(end + 5)..].Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal)) { Flush(); heading = line[3..].Trim(); }
            else if (heading is not null) body.AppendLine(line);
        }
        Flush();
        if (sections.Count == 0) throw new FormatException($"{name}: no tiene secciones ##");
        return new(id, Field("title"), audience, links, sections, fields.TryGetValue("watch", out var w) ? List(w) : []);
    }

    private static string[] List(string value) =>
        value.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The best sections for <paramref name="query"/>; panel-only articles only when <paramref name="includePanel"/>.</summary>
    public IReadOnlyList<KnowledgeHit> Search(string query, bool includePanel, int take = 4)
    {
        var terms = Tokens(query).Distinct().ToList();
        if (terms.Count == 0) return [];
        var n = _docs.Count;
        return _docs.Where(d => includePanel || d.Article.Audience == Public)
            .Select(d =>
            {
                var score = 0.0;
                foreach (var term in terms)
                {
                    if (!d.Terms.TryGetValue(term, out var tf)) continue;
                    var df = _documentFrequency[term];
                    var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                    score += idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * d.Length / _averageLength));
                }
                return (d, score);
            })
            .Where(x => x.score >= MinScore)
            .OrderByDescending(x => x.score)
            .Take(take)
            .Select(x => new KnowledgeHit(x.d.Article.Id, x.d.Article.Title, x.d.Section.Heading, x.d.Section.Text, x.d.Article.Links, Math.Round(x.score, 2)))
            .ToList();
    }

    /// <summary>Lower case, no accents, e-CF as one word, no stop words, light stemming (plural and gender: cajeras → cajer, cajero → cajer).</summary>
    public static List<string> Tokens(string text)
    {
        var folded = Fold(text);
        var tokens = new List<string>();
        var word = new StringBuilder();
        void Emit()
        {
            if (word.Length == 0) return;
            var w = word.ToString();
            word.Clear();
            w = Synonyms.GetValueOrDefault(w, w);
            if (w.Length < 2 || StopWords.Contains(w)) return;
            tokens.Add(Stem(w));
            // TK-348 (evals): the light stemmer keeps verbs apart from their nouns ("instalo" never met "instalación"). A 6-letter prefix
            // term joins them; it is a separate term, so an exact stem still scores higher.
            if (w.Length > 6) tokens.Add("~" + w[..6]);
        }
        for (var i = 0; i < folded.Length; i++)
        {
            var c = folded[i];
            // e-CF, e-NCF: the hyphen joins them, so "e" alone (a stop word) does not swallow them.
            if (c == '-' && word.Length == 1 && word[0] == 'e' && i + 1 < folded.Length && char.IsLetter(folded[i + 1])) continue;
            if (char.IsLetterOrDigit(c)) word.Append(c);
            else Emit();
        }
        Emit();
        return tokens;
    }

    private static string Fold(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Lucene's Spanish light stemmer: plural and gender endings only, so it never merges unrelated words.</summary>
    private static string Stem(string w)
    {
        if (w.Length < 5) return w;
        return w[^1] switch
        {
            'o' or 'a' or 'e' => w[..^1],
            's' when w.EndsWith("eses", StringComparison.Ordinal) => w[..^2],
            's' when w.EndsWith("ces", StringComparison.Ordinal) => w[..^3] + "z",
            's' when w[^2] is 'o' or 'a' or 'e' => w[..^2],
            _ => w,
        };
    }

    /// <summary>Words people use for the same thing, folded to the one the articles use.</summary>
    private static readonly Dictionary<string, string> Synonyms = new()
    {
        ["cuesta"] = "precio", ["cuestan"] = "precio", ["costo"] = "precio", ["costos"] = "precio", ["vale"] = "precio", ["valen"] = "precio",
        ["tarifa"] = "precio", ["tarifas"] = "precio", ["mensualidad"] = "precio", ["cobran"] = "precio", ["precios"] = "precio",
        // TK-312: the site says "ventas a crédito" and "cuentas por cobrar", never "fiado"; people still type it.
        ["pos"] = "caja", ["fiado"] = "cobrar", ["fiados"] = "cobrar", ["fiar"] = "cobrar", ["fia"] = "cobrar", ["fiao"] = "cobrar",
        ["apagon"] = "internet", ["apagones"] = "internet", ["offline"] = "internet", ["conexion"] = "internet",
        ["ordenador"] = "computadora", ["computador"] = "computadora", ["laptop"] = "computadora",
        ["whatsapp"] = "contacto", ["telefono"] = "contacto", ["llamar"] = "contacto", ["soporte"] = "contacto",
    };

    private static readonly HashSet<string> StopWords =
    [
        "a", "al", "algo", "algun", "alguna", "alguno", "ante", "antes", "asi", "aun", "bien", "cada", "como", "con", "cual", "cuando",
        "de", "del", "desde", "donde", "dos", "el", "ella", "ellos", "en", "entre", "era", "es", "esa", "ese", "eso", "esta", "estan", "este",
        "esto", "estoy", "fue", "ha", "hace", "hay", "la", "las", "le", "les", "lo", "los", "mas", "me", "mi", "mis", "mucho", "muy", "nada",
        "ni", "no", "nos", "o", "otra", "otro", "para", "pero", "poco", "por", "porque", "puede", "puedo", "que", "quiero", "se", "ser", "si",
        "sin", "sobre", "son", "su", "sus", "tambien", "tan", "tengo", "tiene", "todo", "todos", "tu", "tus", "un", "una", "uno", "unos", "usted",
        "y", "ya", "yo", "hola", "buenas", "buenos", "favor", "gracias", "saber", "quisiera", "necesito", "dime", "digame", "abastalia",
    ];
}

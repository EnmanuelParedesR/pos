using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Identity;
using Azure.Storage.Blobs;
using Abastalia.Host.Cloud.Accounts;
using Abastalia.Host.Cloud.Billing;

namespace Abastalia.Host.Cloud.Assistant;

/// <summary>TK-350 (docs/talia.md §5): where Talia's knowledge comes from once it can change without a deploy. Section <c>Talia:Knowledge</c>.</summary>
public sealed class TaliaKnowledgeOptions
{
    /// <summary>The blob container with the articles (<c>talia-knowledge</c> in Azure, on the archive's account); empty = the compiled base only.</summary>
    public string? Container { get; set; }

    /// <summary>The storage account; empty = <c>Archive:Blob:ServiceUri</c>.</summary>
    public string? ServiceUri { get; set; }

    /// <summary>A folder with the articles instead of a container (development and tests).</summary>
    public string? Folder { get; set; }

    public int RefreshMinutes { get; set; } = 10;

    /// <summary>Hours between checks of the outside pages the articles watch (the DGII's); 0 = never.</summary>
    public int WatchHours { get; set; } = 24;

    /// <summary>US$ → RD$ for live prices, as the site's <c>config.ts</c> (<c>dopPerUsd</c>); a test keeps both the same.</summary>
    public decimal DopPerUsd { get; set; } = 61.5m;
}

/// <summary>The base Talia reads right now. Starts as the compiled one; <see cref="TaliaKnowledgeRefresher"/> swaps in a newer one when it is valid.</summary>
public sealed class TaliaKnowledgeStore
{
    private volatile TaliaKnowledge _current = TaliaKnowledge.Embedded;

    public TaliaKnowledge Current => _current;

    /// <summary>"compilada", "blob" or "carpeta", plus "+precios en línea" when Paddle's prices replaced the provisional ones.</summary>
    public string Source { get; private set; } = "compilada";

    internal void Replace(TaliaKnowledge knowledge, string source) => (_current, Source) = (knowledge, source);
}

/// <summary>The articles and the watcher's state outside the API: a blob container in Azure, a folder elsewhere.</summary>
public interface ITaliaKnowledgeFiles
{
    /// <summary>Every <c>*.md</c> (name without .md, content); empty when there are none.</summary>
    Task<IReadOnlyList<(string Name, string Markdown)>> ReadArticlesAsync(CancellationToken ct);

    Task<string?> ReadStateAsync(CancellationToken ct);

    Task WriteStateAsync(string json, CancellationToken ct);

    string Kind { get; }
}

internal sealed class BlobKnowledgeFiles(BlobContainerClient container) : ITaliaKnowledgeFiles
{
    private const string State = "_state/watch.json";

    public string Kind => "blob";

    public async Task<IReadOnlyList<(string Name, string Markdown)>> ReadArticlesAsync(CancellationToken ct)
    {
        var files = new List<(string, string)>();
        await foreach (var blob in container.GetBlobsAsync(cancellationToken: ct))
        {
            if (!blob.Name.EndsWith(".md", StringComparison.Ordinal) || blob.Name.Contains('/', StringComparison.Ordinal) || blob.Name.StartsWith('_')) continue;
            var content = await container.GetBlobClient(blob.Name).DownloadContentAsync(ct);
            files.Add((blob.Name[..^3], content.Value.Content.ToString()));
        }
        return files;
    }

    public async Task<string?> ReadStateAsync(CancellationToken ct)
    {
        var blob = container.GetBlobClient(State);
        return await blob.ExistsAsync(ct) ? (await blob.DownloadContentAsync(ct)).Value.Content.ToString() : null;
    }

    public Task WriteStateAsync(string json, CancellationToken ct) => container.GetBlobClient(State).UploadAsync(BinaryData.FromString(json), overwrite: true, ct);
}

internal sealed class FolderKnowledgeFiles(string folder) : ITaliaKnowledgeFiles
{
    public string Kind => "carpeta";

    public async Task<IReadOnlyList<(string Name, string Markdown)>> ReadArticlesAsync(CancellationToken ct)
    {
        if (!Directory.Exists(folder)) return [];
        var files = new List<(string, string)>();
        foreach (var path in Directory.GetFiles(folder, "*.md").Where(p => !Path.GetFileName(p).StartsWith('_')).Order(StringComparer.Ordinal))
            files.Add((Path.GetFileNameWithoutExtension(path), await File.ReadAllTextAsync(path, ct)));
        return files;
    }

    public async Task<string?> ReadStateAsync(CancellationToken ct) => File.Exists(StatePath) ? await File.ReadAllTextAsync(StatePath, ct) : null;

    public Task WriteStateAsync(string json, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        return File.WriteAllTextAsync(StatePath, json, ct);
    }

    private string StatePath => Path.Combine(folder, "_watch-state.json");
}

/// <summary>TK-350: Paddle's prices in the <c>precios</c> article once billing is on, so Talia never quotes the provisional ones of the site's
/// <c>config.ts</c> after the real ones exist. Replaces the plan, add-on and annual sections (their headings are part of the article's contract,
/// <see cref="Headings"/>); installation, accompaniment and the promotion stay as written.</summary>
public static class TaliaLivePrices
{
    public const string ArticleId = "precios";

    /// <summary>The first three headings of <c>Knowledge/precios.md</c>, in order (the knowledge tests check they are there).</summary>
    public static readonly string[] Headings =
    [
        "¿Cuánto cuesta Abastalia? Precios de los planes",
        "¿Cuánto cuestan los adicionales? Caja adicional, cajero adicional, sucursal",
        "¿Hay descuento por pago anual? Precio al año",
    ];

    /// <summary>The article with live prices, or null when billing is off or Paddle has no prices yet (the provisional ones stay).</summary>
    public static KnowledgeArticle? Build(KnowledgeArticle provisional, IReadOnlyList<BillingOption> plans, IReadOnlyList<BillingOption> addOns, decimal dopPerUsd)
    {
        if (plans.Count == 0 || plans.All(p => p.Monthly is null)) return null;
        string Price(CatalogPrice price, string per)
        {
            if (!decimal.TryParse(price.Amount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cents)) return "";
            var amount = cents / 100;
            if (price.Currency != "USD") return $"{price.Currency} {amount.ToString("N2", CultureInfo.InvariantCulture)} {per}";
            // As the site shows it (livePrices.ts, sitePrice): RD$ rounded to 5, or to 10 from RD$1,000.
            var dop = amount * dopPerUsd;
            var step = dop < 1000 ? 5 : 10;
            var usd = amount.ToString(amount == decimal.Truncate(amount) ? "0" : "0.00", CultureInfo.InvariantCulture);
            return $"RD${(Math.Round(dop / step, MidpointRounding.AwayFromZero) * step).ToString("N0", CultureInfo.InvariantCulture)} {per} (se cobra US${usd} con tarjeta)";
        }
        string Line(BillingOption o, Func<BillingOption, CatalogPrice?> pick, string per) => pick(o) is { } p ? $"{o.Name}: {Price(p, per)}." : "";

        var monthly = string.Join(" ", plans.Select(p => Line(p, o => o.Monthly, "al mes")).Where(l => l.Length > 0));
        var extras = string.Join(" ", addOns.Select(a => Line(a, o => o.Monthly, "al mes")).Where(l => l.Length > 0));
        var annual = string.Join(" ", plans.Concat(addOns).Select(p => Line(p, o => o.Annual, "al año")).Where(l => l.Length > 0));
        var live = new[]
        {
            $"Precios vigentes de los planes de Abastalia: {monthly} Es el precio final; se cobra con tarjeta en línea y el detalle está en /precios.",
            extras.Length > 0 ? $"Precios vigentes de los adicionales, por cantidad: {extras} Se agregan en Plan y facturación del panel." : "",
            annual.Length > 0 ? $"Precios vigentes al pagar por año: {annual}" : "",
        };
        var sections = provisional.Sections.Select(s => Array.IndexOf(Headings, s.Heading) is var i and >= 0
                ? live[i].Length > 0 ? s with { Text = live[i] } : null
                : s)
            .OfType<KnowledgeSection>().ToList();
        return provisional with { Sections = sections };
    }
}

/// <summary>TK-350 (Fase 1): every <c>Talia:Knowledge:RefreshMinutes</c>, reads the articles from Blob Storage (or a folder) and, if they pass
/// <see cref="TaliaKnowledge.Problems"/>, Talia uses them; if not, she keeps the last good base and the log says why. Then Paddle's prices replace
/// the provisional ones. In the API itself, not an Azure Function: it costs nothing and needs no deploy of its own.</summary>
internal sealed class TaliaKnowledgeRefresher(TaliaKnowledgeStore store, TaliaKnowledgeOptions options, IServiceProvider services, ILogger<TaliaKnowledgeRefresher> log)
    : BackgroundService
{
    private string? _lastVersion;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(options.RefreshMinutes, 1)));
        do
        {
            try { await RefreshAsync(stoppingToken); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Talia knowledge refresh failed; keeping {Version}", store.Current.Version); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        var (knowledge, source) = (TaliaKnowledge.Embedded, "compilada");
        if (services.GetService<ITaliaKnowledgeFiles>() is { } files && await files.ReadArticlesAsync(ct) is { Count: > 0 } articles)
        {
            TaliaKnowledge? candidate = null;
            IReadOnlyList<string> problems;
            try { problems = (candidate = TaliaKnowledge.Parse(articles)).Problems(); }
            catch (FormatException e) { problems = [e.Message]; }
            if (problems.Count == 0) (knowledge, source) = (candidate!, files.Kind);
            else
            {
                log.LogError("Talia knowledge from {Kind} rejected, keeping the current base: {Problems}", files.Kind, string.Join(" | ", problems.Take(10)));
                if (store.Source != "compilada") return; // keep the last good outside base, not the compiled one
            }
        }

        if (services.GetService<PaddleOptions>() is { IsConfigured: true } && knowledge.Articles.FirstOrDefault(a => a.Id == TaliaLivePrices.ArticleId) is { } prices)
        {
            var (plans, addOns) = services.GetRequiredService<Quotas.PlanLimits>().Catalog();
            var catalog = services.GetRequiredService<BillingCatalog>();
            BillingOption? Option(string code) => catalog.Of(code) is { } e
                ? new BillingOption(code, e.Name, catalog.PriceOf(code, BillingInterval.Monthly), catalog.PriceOf(code, BillingInterval.Annual)) : null;
            if (TaliaLivePrices.Build(prices, [.. plans.Select(Option).OfType<BillingOption>()], [.. addOns.Select(Option).OfType<BillingOption>()], options.DopPerUsd) is { } live)
                (knowledge, source) = (knowledge.With(live, "paddle"), source + "+precios en línea");
        }

        store.Replace(knowledge, source);
        if (knowledge.Version != _lastVersion) log.LogInformation("Talia knowledge {Version} from {Source}: {Articles} articles", knowledge.Version, source, knowledge.Articles.Count);
        _lastVersion = knowledge.Version;
    }
}

/// <summary>TK-350 (Fase 3, the cheap half): once a day, fetches the outside pages the articles list under <c>watch:</c> (the DGII's e-CF pages)
/// and, when one's text changed since the last check, tells Abastalia staff by e-mail (<c>Certification:StaffEmails</c>) and the log. It never
/// edits the base: a DGII change needs a person to read it (a wrong date there is the customer's legal problem).</summary>
internal sealed partial class TaliaSourceWatcher(TaliaKnowledgeStore store, TaliaKnowledgeOptions options, IServiceProvider services, IConfiguration config,
    TimeProvider clock, ILogger<TaliaSourceWatcher> log) : BackgroundService
{
    public const string HttpClientName = "talia-watch";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.WatchHours <= 0) return;
        await Task.Delay(TimeSpan.FromMinutes(5), clock, stoppingToken); // after the start-up rush
        using var timer = new PeriodicTimer(TimeSpan.FromHours(options.WatchHours), clock);
        do
        {
            try { await CheckAsync(stoppingToken); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Talia source check failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Returns the pages whose text changed (a page seen for the first time is only remembered).</summary>
    public async Task<IReadOnlyList<string>> CheckAsync(CancellationToken ct)
    {
        var files = services.GetService<ITaliaKnowledgeFiles>();
        var state = (files is null ? null : await files.ReadStateAsync(ct)) is { } json
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [] : _memory;
        var watched = store.Current.Articles.SelectMany(a => (a.Watch ?? []).Select(url => (url, a.Id))).GroupBy(x => x.url)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(x => x.Id)));
        var http = services.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
        var changed = new List<string>();
        foreach (var (url, articles) in watched)
        {
            string hash;
            try
            {
                using var response = await http.GetAsync(url, ct);
                if (response.StatusCode != HttpStatusCode.OK) { log.LogWarning("Talia source {Url} answered {Status}", url, (int)response.StatusCode); continue; }
                hash = Hash(await response.Content.ReadAsStringAsync(ct));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                log.LogWarning(e, "Talia source {Url} could not be read", url);
                continue;
            }
            if (state.TryGetValue(url, out var before) && before != hash)
            {
                changed.Add(url);
                log.LogWarning("Talia source changed: {Url} (articles {Articles}); review them", url, articles);
                await TellStaffAsync(url, articles, ct);
            }
            state[url] = hash;
        }
        if (files is not null) await files.WriteStateAsync(JsonSerializer.Serialize(state), ct);
        return changed;
    }

    private readonly Dictionary<string, string> _memory = [];

    private async Task TellStaffAsync(string url, string articles, CancellationToken ct)
    {
        if (services.GetService<IEmailSender>() is not { } email) return;
        var staff = config.GetSection("Certification:StaffEmails").Get<string[]>()?.Where(e => !string.IsNullOrWhiteSpace(e)) ?? [];
        var body = $"Cambió una página de la DGII que Talia usa:\n{url}\n\nRevise y actualice estos artículos de la base de Talia: {articles} " +
                   "(src/Hosts/Abastalia.Host.Cloud/Assistant/Knowledge, luego deploy/azure/talia-knowledge-publish.ps1). Talia sigue contestando con lo que dice hoy la base.";
        foreach (var to in staff)
        {
            try { await email.SendAsync(to, "Abastalia · Talia: cambió una página de la DGII", body, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Talia source change could not be e-mailed"); }
        }
    }

    /// <summary>The page's visible text only: tags, scripts, styles and SharePoint's per-request values out, so only a real change counts.</summary>
    internal static string Hash(string html)
    {
        var text = Noise().Replace(html, " ");
        text = Tags().Replace(text, " ");
        text = Spaces().Replace(WebUtility.HtmlDecode(text), " ").Trim();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    [GeneratedRegex(@"<(script|style|noscript)\b[\s\S]*?</\1>|<input[^>]*type=""hidden""[^>]*>|<!--[\s\S]*?-->", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

internal static class TaliaUpdatesSetup
{
    /// <summary>TK-350: the store always; the outside base, the refresher and the watcher as configured (docs/talia.md §4).</summary>
    public static void AddTaliaUpdates(this IServiceCollection services, IConfiguration config)
    {
        var options = config.GetSection("Talia:Knowledge").Get<TaliaKnowledgeOptions>() ?? new TaliaKnowledgeOptions();
        services.AddSingleton(options);
        services.AddSingleton<TaliaKnowledgeStore>();
        if (options.Container is { Length: > 0 } container && (options.ServiceUri ?? config["Archive:Blob:ServiceUri"]) is { Length: > 0 } uri)
            services.AddSingleton<ITaliaKnowledgeFiles>(new BlobKnowledgeFiles(new BlobServiceClient(new Uri(uri), new DefaultAzureCredential()).GetBlobContainerClient(container)));
        else if (options.Folder is { Length: > 0 } folder)
            services.AddSingleton<ITaliaKnowledgeFiles>(new FolderKnowledgeFiles(folder));
        services.AddSingleton<TaliaKnowledgeRefresher>();
        services.AddHostedService(sp => sp.GetRequiredService<TaliaKnowledgeRefresher>());
        services.AddSingleton<TaliaSourceWatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<TaliaSourceWatcher>());
        // The DGII's firewall turns away curl's User-Agent; Abastalia says who it is, as the store does (FiscalModule, TK-252).
        services.AddHttpClient(TaliaSourceWatcher.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("AbastaliaPOS/1.0 (+https://github.com/EnmanuelParedesR/pos)");
        });
    }
}

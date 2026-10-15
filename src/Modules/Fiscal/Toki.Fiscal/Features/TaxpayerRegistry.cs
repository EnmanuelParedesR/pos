using System.Data;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Fiscal.Domain;
using Toki.SharedKernel;

namespace Toki.Fiscal.Features;

/// <summary>DGII taxpayer registry: the owner uploads DGII_RNC.TXT (pipe-separated, ANSI) and the register looks names up by RNC or cédula.</summary>
internal static class TaxpayerRegistry
{
    private const int BatchSize = 5000;
    private const long MaxFileBytes = 300_000_000;

    /// <summary>Where the background import is (TK-162). <c>Percent</c> is how much of the file was read.</summary>
    public sealed record ImportStatus(bool Running, int Percent, int Taxpayers, string? Error, DateTime? FinishedUtc);
    public sealed record Response(string Rnc, string Name, string? TradeName, bool IsActive);

    /// <summary>"RNC|RAZÓN SOCIAL|NOMBRE COMERCIAL|…|ESTADO|…". Null for the header, blank or malformed lines.</summary>
    /// <remarks>shortcut: the state is read from column 10 as DGII publishes it today; check the layout when DGII changes the file.</remarks>
    public static Taxpayer? Read(string line)
    {
        var c = line.Split('|');
        var rnc = Rnc.Normalize(c[0]);
        if (c.Length < 2 || !Rnc.IsValid(rnc) || string.IsNullOrWhiteSpace(c[1])) return null;
        string? Field(int i) => i < c.Length && c[i].Trim() is { Length: > 0 } v ? (v.Length > 250 ? v[..250] : v) : null;
        return new Taxpayer
        {
            Rnc = rnc!, Name = Field(1)!, TradeName = Field(2),
            IsActive = Field(9) is not { } status || status.Equals("ACTIVO", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>DGII has published the file as ANSI (Windows-1252); if a future one is UTF-8, its first 64 KB decode strictly and it is read as such.</summary>
    internal static async Task<Encoding> DetectEncodingAsync(Stream file, CancellationToken ct)
    {
        var head = new byte[64 * 1024];
        var read = await file.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        // A multi-byte character may be cut at the end of the sample: leave its last 3 bytes out.
        var sample = head.AsSpan(0, read == head.Length ? read - 3 : read);
        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetCharCount(sample);
            return Encoding.UTF8;
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1;
        }
    }

    /// <summary>Replaces the whole registry with the file; a failure leaves the previous one. Returns how many taxpayers it loaded.</summary>
    /// <param name="progress">Called after each batch with the taxpayers loaded so far.</param>
    internal static async Task<int> ImportAsync(FiscalDbContext db, DbProvider provider, Stream file, Action<int> progress, CancellationToken ct)
    {
        var encoding = await DetectEncodingAsync(file, ct);
        file.Position = 0;
        using var reader = new StreamReader(file, encoding);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
        if (provider == DbProvider.SqlServer) await BulkCopyAsync(db, Batches(reader, seen, ct), () => progress(seen.Count), ct);
        else await InsertAsync(db, Batches(reader, seen, ct), () => progress(seen.Count), ct);
        return seen.Count;
    }

    private static async IAsyncEnumerable<List<Taxpayer>> Batches(StreamReader reader, HashSet<string> seen, [EnumeratorCancellation] CancellationToken ct)
    {
        var batch = new List<Taxpayer>(BatchSize);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (Read(line) is not { } t || !seen.Add(t.Rnc)) continue;
            batch.Add(t);
            if (batch.Count < BatchSize) continue;
            yield return batch;
            batch = new(BatchSize);
        }
        if (batch.Count > 0) yield return batch;
    }

    /// <summary>Sqlite (development, tests): EF batches in one transaction.</summary>
    private static async Task InsertAsync(FiscalDbContext db, IAsyncEnumerable<List<Taxpayer>> batches, Action progress, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Taxpayers.ExecuteDeleteAsync(ct);
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        await foreach (var batch in batches)
        {
            db.Taxpayers.AddRange(batch);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            progress();
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>SQL Server (stores): SqlBulkCopy into a staging table outside any transaction, so lookups and checkouts keep reading the
    /// old registry meanwhile; then one short transaction swaps the rows in.</summary>
    /// <remarks>shortcut: the swap still locks the table for the few seconds the INSERT … SELECT of ~700k rows takes; a checkout asking
    /// for a buyer's name waits that long.</remarks>
    private static async Task BulkCopyAsync(FiscalDbContext db, IAsyncEnumerable<List<Taxpayer>> batches, Action progress, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            "DROP TABLE IF EXISTS fiscal.TaxpayersImport; SELECT TOP 0 Rnc, Name, TradeName, IsActive INTO fiscal.TaxpayersImport FROM fiscal.Taxpayers;", ct);
        try
        {
            using var bulk = new SqlBulkCopy((SqlConnection)db.Database.GetDbConnection()) { DestinationTableName = "fiscal.TaxpayersImport", BulkCopyTimeout = 300 };
            using var table = new DataTable();
            table.Columns.Add(nameof(Taxpayer.Rnc), typeof(string));
            table.Columns.Add(nameof(Taxpayer.Name), typeof(string));
            table.Columns.Add(nameof(Taxpayer.TradeName), typeof(string));
            table.Columns.Add(nameof(Taxpayer.IsActive), typeof(bool));
            foreach (DataColumn column in table.Columns) bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            await foreach (var batch in batches)
            {
                table.Clear();
                foreach (var t in batch) table.Rows.Add(t.Rnc, t.Name, (object?)t.TradeName ?? DBNull.Value, t.IsActive);
                await bulk.WriteToServerAsync(table, ct);
                progress();
            }
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync(
                "TRUNCATE TABLE fiscal.Taxpayers; INSERT INTO fiscal.Taxpayers WITH (TABLOCK) (Rnc, Name, TradeName, IsActive) SELECT Rnc, Name, TradeName, IsActive FROM fiscal.TaxpayersImport;", ct);
            await tx.CommitAsync(ct);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS fiscal.TaxpayersImport;", CancellationToken.None);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            // TK-162: the real file (~700k rows) takes longer than a request should: the upload is saved and imported in the background.
            app.MapPost("/api/fiscal/taxpayer-registry", async (HttpContext http, TaxpayerImport import, CancellationToken ct) =>
                {
                    if (import.Status.Running) return EndpointExtensions.Problem(TaxpayerImport.AlreadyRunning);
                    if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxFileBytes;
                    var path = Path.Combine(Path.GetTempPath(), $"toki-rnc-{Guid.NewGuid():N}.txt");
                    try
                    {
                        await using (var file = File.Create(path)) await http.Request.Body.CopyToAsync(file, ct);
                        if (import.TryStart(path)) return Results.Accepted("/api/fiscal/taxpayer-registry/import", import.Status);
                    }
                    catch
                    {
                        File.Delete(path);
                        throw;
                    }
                    File.Delete(path);
                    return EndpointExtensions.Problem(TaxpayerImport.AlreadyRunning);
                })
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");

            app.MapGet("/api/fiscal/taxpayer-registry/import", (TaxpayerImport import) => Results.Ok(import.Status))
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");

            app.MapGet("/api/fiscal/taxpayers/{rnc}", async (string rnc, FiscalDbContext db, CancellationToken ct) =>
                    await db.Taxpayers.AsNoTracking().Where(c => c.Rnc == (Rnc.Normalize(rnc) ?? "")).Select(c => new Response(c.Rnc, c.Name, c.TradeName, c.IsActive))
                        .FirstOrDefaultAsync(ct) is { } r
                        ? Results.Ok(r)
                        : EndpointExtensions.Problem(Error.NotFound("Fiscal.RncNotFound", "Ese RNC o cédula no está en el padrón de la DGII.")))
                .RequireAuthorization().WithTags("Fiscal");
        }
    }
}

/// <summary>TK-162: one registry import at a time, off the request and in its own scope; the admin screen polls <see cref="Status"/>.</summary>
internal sealed class TaxpayerImport(IServiceScopeFactory scopes, IHostApplicationLifetime life, TimeProvider clock, ILogger<TaxpayerImport> log)
{
    public static readonly Error AlreadyRunning = Error.Conflict("Fiscal.RegistryImportRunning", "Ya se está cargando un padrón. Espere a que termine.");

    private readonly Lock _gate = new();
    private bool _running;
    private string? _error;
    private DateTime? _finishedUtc;
    private int _taxpayers;
    private long _read, _length;

    public TaxpayerRegistry.ImportStatus Status
    {
        get
        {
            lock (_gate)
                return new(_running, _length == 0 ? 0 : (int)(Interlocked.Read(ref _read) * 100 / _length), Volatile.Read(ref _taxpayers), _error, _finishedUtc);
        }
    }

    /// <summary>Starts importing <paramref name="path"/>, which is deleted afterwards. False when one is already running.</summary>
    public bool TryStart(string path)
    {
        lock (_gate)
        {
            if (_running) return false;
            (_running, _error, _finishedUtc, _taxpayers, _read, _length) = (true, null, null, 0, 0, new FileInfo(path).Length);
        }
        _ = Task.Run(() => RunAsync(path, life.ApplicationStopping));
        return true;
    }

    private async Task RunAsync(string path, CancellationToken ct)
    {
        string? error = null;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
            var count = await TaxpayerRegistry.ImportAsync(scope.ServiceProvider.GetRequiredService<FiscalDbContext>(),
                scope.ServiceProvider.GetRequiredService<DatabaseOptions>().Provider, file, n =>
                {
                    Volatile.Write(ref _taxpayers, n);
                    Interlocked.Exchange(ref _read, file.Position);
                }, ct);
            Volatile.Write(ref _taxpayers, count);
            Interlocked.Exchange(ref _read, _length);
            log.LogInformation("RNC taxpayer registry imported: {Count} taxpayers", count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            error = "La carga se detuvo porque el servidor se apagó. El padrón anterior sigue igual; cárguelo de nuevo.";
        }
        catch (Exception e)
        {
            log.LogError(e, "RNC taxpayer registry import failed");
            error = "No se pudo cargar el padrón. El anterior sigue igual. Revise que sea el archivo DGII_RNC.TXT e intente de nuevo.";
        }
        finally
        {
            try { File.Delete(path); }
            catch (IOException e) { log.LogWarning(e, "Could not delete the uploaded registry {Path}", path); }
            lock (_gate) (_running, _error, _finishedUtc) = (false, error, clock.GetUtcNow().UtcDateTime);
        }
    }
}

using System.Collections.Concurrent;
using System.Xml.Linq;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Fiscal.Contracts;
using Abastalia.Fiscal.Domain;
using Abastalia.Settings.Contracts;
using Abastalia.SharedKernel;

namespace Abastalia.Fiscal;

/// <summary>TK-238: the DGII environment the owner chose in "Comprobantes electrónicos". One row; without it the store follows appsettings
/// (<c>Fiscal:Signer</c>, <c>Fiscal:Provider</c>, <c>Fiscal:Dgii:Environment</c>, <c>Fiscal:AutoSubmit</c>).</summary>
internal sealed class FiscalModeSetting
{
    public int Id { get; set; } = 1;
    public FiscalEnvironment Environment { get; set; }
    public DateTime ChangedUtc { get; set; }
}

internal sealed class FiscalModeConfiguration : IEntityTypeConfiguration<FiscalModeSetting>
{
    public void Configure(EntityTypeBuilder<FiscalModeSetting> b)
    {
        b.ToTable("Mode");
        b.Property(m => m.Id).ValueGeneratedNever();
        b.Property(m => m.Environment).HasConversion<string>().HasMaxLength(10);
    }
}

/// <summary>The saved environment, in memory: the signer, the provider and the queue workers read it on every use, so a change applies without a
/// restart. Loaded at startup (<see cref="FiscalModeLoader"/>) and after a restore. A saved environment other than Simulated also sends on its own.</summary>
internal sealed class FiscalMode(IConfiguration config)
{
    private volatile Saved? _saved;
    private sealed record Saved(FiscalEnvironment Environment);

    public FiscalEnvironment? Environment => _saved?.Environment;
    public bool AutoSubmit => Environment is { } env ? env != FiscalEnvironment.Simulated : config.GetValue<bool>("Fiscal:AutoSubmit");

    public void Set(FiscalEnvironment? env) => _saved = env is { } e ? new Saved(e) : null;

    public async Task LoadAsync(FiscalDbContext db, CancellationToken ct) =>
        Set(await db.Set<FiscalModeSetting>().AsNoTracking().Select(m => (FiscalEnvironment?)m.Environment).FirstOrDefaultAsync(ct));
}

/// <summary>Reads the saved environment before the workers start. A database that cannot be read stops the start (as the migrations do):
/// a store in production must never fall back to the appsettings' simulated mode.</summary>
internal sealed class FiscalModeLoader(IServiceScopeFactory scopes, FiscalMode mode) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await mode.LoadAsync(scope.ServiceProvider.GetRequiredService<FiscalDbContext>(), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Signs with the appsettings' signer until an environment is saved; then Simulated signs with the stand-in and the rest with the certificate.</summary>
internal sealed class ModeEcfSigner(FiscalMode mode, IEcfSigner configured, CertificateEcfSigner certificate) : IEcfSigner
{
    private static readonly SimulatedEcfSigner Simulated = new();

    private IEcfSigner Current => mode.Environment switch
    {
        null => configured,
        FiscalEnvironment.Simulated => Simulated,
        _ => certificate,
    };

    public bool IsSimulated => Current.IsSimulated;
    public string? CannotSign(DateTime nowUtc) => Current.CannotSign(nowUtc);
    public (string SignedXml, string SecurityCode) Sign(XDocument xml) => Current.Sign(xml);
}

/// <summary>Sends to the appsettings' environment until one is saved; one DGII client per environment, made on first use.</summary>
internal sealed class ModeEcfProvider(FiscalMode mode, FiscalEnvironment configured, Func<FiscalEnvironment, DgiiClient> client) : IEcfProvider, IDisposable
{
    private readonly ConcurrentDictionary<FiscalEnvironment, DgiiClient> _clients = new();

    private IEcfProvider Current => For(mode.Environment ?? configured);

    /// <summary>R10 #2: each document goes to the environment it was signed for, even after the store changed environment.</summary>
    public IEcfProvider For(FiscalEnvironment environment) =>
        environment == FiscalEnvironment.Simulated ? SimulatedEcfProvider.Instance : _clients.GetOrAdd(environment, client);

    /// <summary>The DGII client of the current environment (directory lookups), or null while simulated.</summary>
    public DgiiClient? Dgii => Current as DgiiClient;
    public bool IsSimulated => Current.IsSimulated;
    public FiscalEnvironment Environment => Current.Environment;
    public Task<DgiiAnswer> SendEcfAsync(string issuerRnc, string encf, string signedXml, CancellationToken ct) => Current.SendEcfAsync(issuerRnc, encf, signedXml, ct);
    public Task<DgiiAnswer> GetResultAsync(string trackId, CancellationToken ct) => Current.GetResultAsync(trackId, ct);
    public Task<DgiiAnswer> SendRfceAsync(string issuerRnc, string encf, string signedRfce, CancellationToken ct) => Current.SendRfceAsync(issuerRnc, encf, signedRfce, ct);
    public Task<DgiiAnswer?> FindAsync(string issuerRnc, string encf, string securityCode, EcfChannel channel, CancellationToken ct) =>
        Current.FindAsync(issuerRnc, encf, securityCode, channel, ct);
    public Task<DgiiAnswer> VoidAsync(string issuerRnc, string signedAnecf, CancellationToken ct) => Current.VoidAsync(issuerRnc, signedAnecf, ct);

    public void Dispose()
    {
        foreach (var c in _clients.Values) c.Dispose();
    }
}

/// <summary>TK-238: what stands between the store and each environment. Going back to Simulated only needs an empty queue; a DGII environment needs a
/// valid certificate, the RNC and the address; CerteCF and production need their own e-NCF ranges (TesteCF loads its own); production needs the
/// DGII's verification ticked in the certification assistant. Leaving production once it issued e-CF needs support (R10 #3).</summary>
internal sealed class FiscalModeChecks(FiscalDbContext db, FiscalDocuments documents, EcfCertificate certificate, ISettingsQueries settings, TimeProvider clock,
    IConfiguration config)
{
    /// <summary>"Verificación del RNC por la DGII" in EcfCertificationPanel.razor: the step before the authorization.</summary>
    public const int VerifiedStep = CertificationSteps.Count - 2;

    public async Task<List<string>> BlockersAsync(FiscalEnvironment target, CancellationToken ct)
    {
        var blockers = new List<string>();
        if (target == documents.Environment) return blockers;
        // R10 #3: a store that already issued real e-CF does not go back with one click; support sets Fiscal:AllowLeavingProduction.
        if (documents.Environment == FiscalEnvironment.eCF && !config.GetValue<bool>("Fiscal:AllowLeavingProduction")
            && await db.Documents.AnyAsync(d => d.Environment == FiscalEnvironment.eCF, ct))
            blockers.Add("La tienda ya emitió comprobantes en producción: no puede salir de producción desde aquí. Llame a soporte de Abastalia.");
        var waiting = await db.Documents.CountAsync(d => d.Status == SubmissionStatus.Pending || d.Status == SubmissionStatus.Sent, ct)
            + await db.Voids.CountAsync(v => v.Status == SubmissionStatus.Pending || v.Status == SubmissionStatus.Sent, ct);
        if (waiting > 0)
            blockers.Add($"Hay {waiting} {(waiting == 1 ? "comprobante" : "comprobantes")} del ambiente actual por enviar o esperando la respuesta de la DGII. Espere a que salgan (o anúlelos) antes de cambiar.");
        if (target == FiscalEnvironment.Simulated) return blockers;

        var now = clock.GetUtcNow().UtcDateTime;
        if (certificate.Current is null) blockers.Add("Importe el certificado digital en Configuración → Comprobantes.");
        else if (new CertificateEcfSigner(certificate).CannotSign(now) is { } why) blockers.Add($"El certificado digital no sirve: {why}. Importe uno vigente.");
        var business = await settings.GetBusinessProfileAsync(ct);
        if (!Rnc.IsValid(business.Rnc) || string.IsNullOrWhiteSpace(business.Address))
            blockers.Add("Escriba el RNC y la dirección del negocio en Configuración → Negocio: van en cada comprobante.");
        if (target != FiscalEnvironment.TesteCF)
        {
            var today = BusinessDay.Of(now);
            if (!await db.Sequences.AnyAsync(s => s.Environment == target && s.Next <= s.To && (s.ExpiresOn == null || s.ExpiresOn >= today), ct))
                blockers.Add($"Cargue las secuencias de e-NCF que la DGII le dio para {Name(target)} (Configuración → Comprobantes, eligiendo ese ambiente).");
        }
        if (target == FiscalEnvironment.eCF && !await db.Set<CertificationStep>().AnyAsync(s => s.Step == VerifiedStep && s.Done, ct))
            blockers.Add("Termine la certificación: marque \"Verificación del RNC por la DGII\" en el asistente de certificación cuando la DGII la confirme.");
        return blockers;
    }

    public static string Name(FiscalEnvironment env) => env switch
    {
        FiscalEnvironment.TesteCF => "Pre-certificación (TesteCF)",
        FiscalEnvironment.CerteCF => "Certificación (CerteCF)",
        FiscalEnvironment.eCF => "Producción",
        _ => "Simulado",
    };
}

internal static class GetFiscalMode
{
    public sealed record Target(FiscalEnvironment Environment, IReadOnlyList<string> Blockers);

    /// <param name="Saved">True when the environment was chosen on screen; false while the store follows appsettings.</param>
    public sealed record Response(FiscalEnvironment Environment, bool Saved, bool AutoSubmit, DateTime? ChangedUtc, IReadOnlyList<Target> Targets);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/fiscal/mode", async (FiscalDbContext db, FiscalDocuments documents, FiscalMode mode, FiscalModeChecks checks, CancellationToken ct) =>
                {
                    var targets = new List<Target>();
                    foreach (var env in Enum.GetValues<FiscalEnvironment>()) targets.Add(new Target(env, await checks.BlockersAsync(env, ct)));
                    var changed = await db.Set<FiscalModeSetting>().AsNoTracking().Select(m => (DateTime?)m.ChangedUtc).FirstOrDefaultAsync(ct);
                    return Results.Ok(new Response(documents.Environment, mode.Environment is not null, mode.AutoSubmit, changed, targets));
                })
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}

/// <summary>TK-238: moves the store between Simulado, TesteCF, CerteCF and Producción from the screen. Refused while anything in
/// <see cref="FiscalModeChecks"/> is missing; needs the owner's explicit confirmation; audited. Applies at once (no restart).</summary>
internal static class ChangeFiscalMode
{
    public sealed record Command(FiscalEnvironment Environment, bool Confirm) : ICommand<Ok>;

    public static Error Blocked(IEnumerable<string> blockers) => Error.Conflict("Fiscal.ModeBlocked", string.Join(" ", blockers));

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Environment).IsInEnum();
            RuleFor(c => c.Confirm).Equal(true).WithMessage("Confirme el cambio de ambiente.");
        }
    }

    internal sealed class Handler(FiscalDbContext db, FiscalDocuments documents, FiscalModeChecks checks, IAuditLog audit, TimeProvider clock)
        : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            if (await checks.BlockersAsync(c.Environment, ct) is [_, ..] blockers) return Blocked(blockers);
            var from = documents.Environment;
            var row = await db.Set<FiscalModeSetting>().FirstOrDefaultAsync(ct);
            if (row is null) db.Set<FiscalModeSetting>().Add(row = new FiscalModeSetting());
            (row.Environment, row.ChangedUtc) = (c.Environment, clock.GetUtcNow().UtcDateTime);
            await audit.RecordAsync("fiscal.change-mode", "FiscalMode", Guid.Empty, new { From = from, To = c.Environment }, ct);
            return Ok.Value;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPut("/api/fiscal/mode", async (Command c, ICommandHandler<Command, Ok> h, FiscalMode mode, CancellationToken ct) =>
                {
                    var r = await h.Handle(c, ct);
                    if (r.IsSuccess) mode.Set(c.Environment); // only once committed
                    return r.ToHttp(_ => Results.NoContent());
                })
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}

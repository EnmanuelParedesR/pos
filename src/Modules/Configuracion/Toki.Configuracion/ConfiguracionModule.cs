using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Toki.BuildingBlocks;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Configuracion.Contracts;
using Toki.SharedKernel;

namespace Toki.Configuracion;

public static class ConfiguracionModule
{
    public static IServiceCollection AddConfiguracionModule(this IServiceCollection services, IConfiguration config)
    {
        services.AddModuleDbContext<ConfiguracionDbContext>();
        services.AddScoped<IConfiguracionConsultas, ConfiguracionConsultas>();
        services.AddSingleton<UltimaVerificacionAuditoria>();
        services.AddHostedService<VerificacionNocturnaAuditoria>();
        services.AddHealthChecks().AddCheck<UltimaVerificacionAuditoria>("auditoria").AddCheck<RespaldoHealthCheck>("respaldo");
        // The key ring that protects the backup key lives with the store's data, not in the service account's profile.
        var claves = services.AddDataProtection().SetApplicationName("Toki").PersistKeysToFileSystem(new DirectoryInfo(
            config["Toki:CarpetaClaves"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Toki", "claves")));
        if (OperatingSystem.IsWindows()) claves.ProtectKeysWithDpapi(protectToLocalMachine: true);
        services.AddSingleton<EstadoRespaldo>();
        services.AddScoped<Respaldos>();
        services.AddSingleton<ServicioLicencia>();
        services.AddSingleton<IFeatureService>(sp => sp.GetRequiredService<ServicioLicencia>());
        services.AddHostedService<RespaldoNocturno>();
        return services;
    }
}

internal sealed class Negocio
{
    public int Id { get; set; } = 1;
    public string Nombre { get; set; } = "Mi tienda";
    public string? Rnc { get; set; }
    public string? Direccion { get; set; }
    public string? Telefono { get; set; }
    public string? PieRecibo { get; set; }
    public decimal TopeDescuentoPct { get; set; } = 10;
    /// <summary>Salt of the backup passphrase (in every backup's header too).</summary>
    public byte[]? RespaldoSal { get; set; }
    /// <summary>Key derived from the passphrase, protected for this machine (ASP.NET Core Data Protection). The phrase itself is never stored.</summary>
    public string? RespaldoClave { get; set; }

    public DatosNegocio ADatos() => new(Nombre, Rnc, Direccion, Telefono, PieRecibo, TopeDescuentoPct);
}

internal sealed class ConfiguracionDbContext(DbContextOptions<ConfiguracionDbContext> options) : ModuleDbContext(options)
{
    protected override string Schema => "config";
    public DbSet<Negocio> Negocio => Set<Negocio>();
}

internal sealed class NegocioConfiguration : IEntityTypeConfiguration<Negocio>
{
    public void Configure(EntityTypeBuilder<Negocio> b)
    {
        b.ToTable("Negocio");
        b.Property(n => n.Id).ValueGeneratedNever();
        b.Property(n => n.PieRecibo).HasMaxLength(500);
        b.Property(n => n.RespaldoSal).HasMaxLength(16);
        b.Property(n => n.RespaldoClave).HasMaxLength(1000);
        // Literal values: changing a property default later must not overwrite the owner's settings through a migration.
        b.HasData(new Negocio { Id = 1, Nombre = "Mi tienda", TopeDescuentoPct = 10 });
    }
}

internal sealed class ConfiguracionConsultas(ConfiguracionDbContext db) : IConfiguracionConsultas
{
    public async Task<DatosNegocio> ObtenerNegocioAsync(CancellationToken ct) =>
        (await db.Negocio.AsNoTracking().SingleAsync(ct)).ADatos();
}

internal static class ObtenerNegocio
{
    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/configuracion/negocio", async (IConfiguracionConsultas c, CancellationToken ct) => Results.Ok(await c.ObtenerNegocioAsync(ct)))
                .RequireAuthorization().WithTags("Configuración");
    }
}

internal static class ActualizarNegocio
{
    public sealed record Command(string Nombre, string? Rnc, string? Direccion, string? Telefono, string? PieRecibo, decimal TopeDescuentoPct) : ICommand<Ok>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Nombre).NotEmpty().MaximumLength(150);
            RuleFor(c => c.Rnc).Must(r => SharedKernel.Rnc.EsValido(SharedKernel.Rnc.Normalizar(r))).When(c => !string.IsNullOrWhiteSpace(c.Rnc))
                .WithMessage("El RNC debe tener 9 dígitos (o 11 si es cédula).");
            RuleFor(c => c.Direccion).MaximumLength(200);
            RuleFor(c => c.Telefono).MaximumLength(30);
            RuleFor(c => c.PieRecibo).MaximumLength(500);
            RuleFor(c => c.TopeDescuentoPct).InclusiveBetween(0, 100);
        }
    }

    internal sealed class Handler(ConfiguracionDbContext db, IAuditoria auditoria) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var n = await db.Negocio.SingleAsync(ct);
            n.Nombre = c.Nombre.Trim();
            n.Rnc = SharedKernel.Rnc.Normalizar(c.Rnc);
            n.Direccion = c.Direccion?.Trim();
            n.Telefono = c.Telefono?.Trim();
            n.PieRecibo = c.PieRecibo?.Trim();
            n.TopeDescuentoPct = c.TopeDescuentoPct;
            await auditoria.RegistrarAsync("configuracion.negocio", "Negocio", Guid.Empty, c, ct);
            return Ok.Valor;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPut("/api/configuracion/negocio", async (Command c, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermiso(Permisos.ConfiguracionGestionar).WithTags("Configuración");
    }
}

/// <summary>For dotnet-ef: SQL Server migrations of this module.</summary>
internal sealed class DisenoConfiguracion : Toki.BuildingBlocks.Persistence.DisenoDbContext<ConfiguracionDbContext>;

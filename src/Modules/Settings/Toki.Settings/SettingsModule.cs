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
using Toki.Fiscal.Contracts;
using Toki.Settings.Contracts;
using Toki.SharedKernel;

namespace Toki.Settings;

public static class SettingsModule
{
    public static IServiceCollection AddSettingsModule(this IServiceCollection services, IConfiguration config)
    {
        services.AddModuleDbContext<SettingsDbContext>();
        services.AddScoped<ISettingsQueries, SettingsQueries>();
        services.AddScoped<IApprovalPolicy, ApprovalPolicy>();
        services.AddSingleton<LastAuditCheck>();
        services.AddHostedService<NightlyAuditCheck>();
        services.AddHealthChecks().AddCheck<LastAuditCheck>("audit").AddCheck<BackupHealthCheck>("backup");
        // The key ring that protects the backup key lives with the store's data, not in the service account's profile.
        var keys = services.AddDataProtection().SetApplicationName("Toki").PersistKeysToFileSystem(new DirectoryInfo(
            config["Toki:KeysFolder"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Toki", "keys")));
        if (OperatingSystem.IsWindows()) keys.ProtectKeysWithDpapi(protectToLocalMachine: true);
        services.AddSingleton<BackupStatus>();
        services.AddScoped<Backups>();
        services.AddSingleton<LicenseService>();
        services.AddSingleton<IFeatureService>(sp => sp.GetRequiredService<LicenseService>());
        services.AddScoped<IStoreLicense, StoreLicense>();
        services.AddHostedService<NightlyBackup>();
        return services;
    }
}

internal sealed class Business
{
    public int Id { get; set; } = 1;
    public string Name { get; set; } = "Mi tienda";
    public string? Rnc { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? ReceiptFooter { get; set; }
    public decimal DiscountCapPercent { get; set; } = 10;
    /// <summary>Salt of the backup passphrase (in every backup's header too).</summary>
    public byte[]? BackupSalt { get; set; }
    /// <summary>Key derived from the passphrase, protected for this machine (ASP.NET Core Data Protection). The phrase itself is never stored.</summary>
    public string? BackupKey { get; set; }
    /// <summary>Comma-separated permissions this store asks a supervisor for (TK-210); null until the owner saves it: <see cref="ApprovalPolicy.Defaults"/>.</summary>
    public string? SupervisorApprovals { get; set; }
    /// <summary>TK-196: the first sale, which starts the 14-day trial; null while the store has not sold.</summary>
    public DateTime? TrialStartedUtc { get; set; }
    /// <summary>TK-238: the backup schedule saved on screen; null follows appsettings (<see cref="BackupSchedule"/>). An empty folder means no copy.</summary>
    public string? BackupCopyTo { get; set; }
    public TimeSpan? BackupTime { get; set; }
    public int? BackupKeep { get; set; }
    public bool? BackupAutomatic { get; set; }

    public BusinessProfile ToProfile() => new(Name, Rnc, Address, Phone, ReceiptFooter, DiscountCapPercent);
}

internal sealed class SettingsDbContext(DbContextOptions<SettingsDbContext> options) : ModuleDbContext(options)
{
    protected override string Schema => "settings";
    public DbSet<Business> Business => Set<Business>();
    public DbSet<UserPreference> UserPreferences => Set<UserPreference>();
}

internal sealed class BusinessConfiguration : IEntityTypeConfiguration<Business>
{
    public void Configure(EntityTypeBuilder<Business> b)
    {
        b.ToTable("Business");
        b.Property(n => n.Id).ValueGeneratedNever();
        b.Property(n => n.ReceiptFooter).HasMaxLength(500);
        b.Property(n => n.BackupSalt).HasMaxLength(16);
        b.Property(n => n.BackupKey).HasMaxLength(1000);
        b.Property(n => n.SupervisorApprovals).HasMaxLength(500);
        b.Property(n => n.BackupCopyTo).HasMaxLength(260);
        // Literal values: changing a property default later must not overwrite the owner's settings through a migration.
        b.HasData(new Business { Id = 1, Name = "Mi tienda", DiscountCapPercent = 10 });
    }
}

internal sealed class SettingsQueries(SettingsDbContext db) : ISettingsQueries
{
    public async Task<BusinessProfile> GetBusinessProfileAsync(CancellationToken ct) =>
        (await db.Business.AsNoTracking().SingleAsync(ct)).ToProfile();
}

internal static class GetBusinessProfile
{
    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/settings/business", async (ISettingsQueries q, CancellationToken ct) => Results.Ok(await q.GetBusinessProfileAsync(ct)))
                .RequireAuthorization().WithTags("Settings");
    }
}

internal static class UpdateBusinessProfile
{
    public sealed record Command(string Name, string? Rnc, string? Address, string? Phone, string? ReceiptFooter, decimal DiscountCapPercent) : ICommand<Ok>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Name).NotEmpty().MaximumLength(150);
            RuleFor(c => c.Rnc).Must(r => SharedKernel.Rnc.IsValid(SharedKernel.Rnc.Normalize(r))).When(c => !string.IsNullOrWhiteSpace(c.Rnc))
                .WithMessage("El RNC debe tener 9 dígitos (o 11 si es cédula).");
            RuleFor(c => c.Address).MaximumLength(200);
            RuleFor(c => c.Phone).MaximumLength(30);
            RuleFor(c => c.ReceiptFooter).MaximumLength(500);
            RuleFor(c => c.DiscountCapPercent).InclusiveBetween(0, 100);
        }
    }

    internal sealed class Handler(SettingsDbContext db, IAuditLog audit, IEcfCertificateHolder certificate) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var rnc = SharedKernel.Rnc.Normalize(c.Rnc);
            // R9 #20: the certificate may have been imported before the RNC was set, when there was nothing to compare it with.
            if (certificate.TaxId is { } taxId && !IEcfCertificateHolder.Fits(taxId, rnc))
                return Error.Validation("Settings.CertificateWrongRnc",
                    $"El certificado digital instalado es de {taxId}: con el RNC {rnc} la DGII rechazaría todo lo firmado. Revise el RNC o importe el certificado de este negocio.");
            var b = await db.Business.SingleAsync(ct);
            b.Name = c.Name.Trim();
            b.Rnc = rnc;
            b.Address = c.Address?.Trim();
            b.Phone = c.Phone?.Trim();
            b.ReceiptFooter = c.ReceiptFooter?.Trim();
            b.DiscountCapPercent = c.DiscountCapPercent;
            await audit.RecordAsync("settings.business", "Business", Guid.Empty, c, ct);
            return Ok.Value;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPut("/api/settings/business", async (Command c, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.SettingsManage).WithTags("Settings");
    }
}

/// <summary>For dotnet-ef: SQL Server migrations of this module.</summary>
internal sealed class SettingsDesignTimeFactory : Toki.BuildingBlocks.Persistence.DesignTimeFactory<SettingsDbContext>;

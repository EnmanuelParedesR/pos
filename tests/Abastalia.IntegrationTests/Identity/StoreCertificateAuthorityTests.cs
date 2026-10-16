using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.Identity;
using Abastalia.Identity.Domain;
using Abastalia.Identity.Persistence;
using Abastalia.IntegrationTests.Infra;

namespace Abastalia.IntegrationTests.Identity;

/// <summary>R9 #8 and #15: the store CA's files across restarts, and a CA that cannot be read.</summary>
public sealed class StoreCertificateAuthorityTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private static StoreCertificateAuthority Ca(string folder) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Abastalia:KeysFolder"] = folder }).Build(),
        DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(folder, "ring"))), TimeProvider.System);

    [Fact]
    public void A_restart_shows_the_same_server_certificate()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"abastalia_keys_{Guid.NewGuid():N}");
        string thumbprint;
        using (var first = Ca(folder)) thumbprint = first.ServerCertificate.Thumbprint;
        using var second = Ca(folder);
        second.ServerCertificate.Thumbprint.ShouldBe(thumbprint);
    }

    [Fact]
    public void An_unreadable_CA_is_kept_aside_and_replaced_instead_of_stopping_the_store()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"abastalia_keys_{Guid.NewGuid():N}");
        string before;
        using (var first = Ca(folder)) before = first.CertificatePem;
        File.WriteAllBytes(Path.Combine(folder, "store-ca.key"), RandomNumberGenerator.GetBytes(64)); // damaged, or another PC's key ring

        using var second = Ca(folder);
        second.CertificatePem.ShouldNotBe(before);
        second.ServerCertificate.ShouldNotBeNull();
        Directory.GetFiles(folder, "store-ca.key.unreadable-*").Length.ShouldBe(1);
        Directory.GetFiles(folder, "store-ca.pem.unreadable-*").Length.ShouldBe(1);
    }

    [Fact]
    public async Task Registers_paired_with_a_replaced_CA_show_in_the_health_check()
    {
        await app.AdminAsync();
        var ca = app.Services.GetRequiredService<StoreCertificateAuthority>();
        await app.InScopeAsync(sp =>
        {
            var db = sp.GetRequiredService<IdentityDbContext>();
            db.Devices.Add(new Device { Id = Guid.CreateVersion7(), Name = "Caja vieja", CertificateThumbprint = "AB", CreatedUtc = ca.CreatedUtc.AddDays(-3) });
            return db.SaveChangesAsync();
        });
        var check = await app.InScopeAsync(sp => ActivatorUtilities.CreateInstance<StoreCaHealthCheck>(sp).CheckHealthAsync(new()));
        check.Status.ShouldBe(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded);
        check.Description!.ShouldContain("Caja vieja");
    }
}

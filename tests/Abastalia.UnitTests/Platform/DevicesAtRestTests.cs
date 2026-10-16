using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;
using Abastalia.Host.Local;
using Abastalia.Settings;

namespace Abastalia.UnitTests.Platform;

/// <summary>R11 part C: what the store PC writes down and where the backups may go.</summary>
public sealed class DevicesAtRestTests
{
    [Theory]
    [InlineData("/api/fiscal/taxpayers/131245678", "/api/fiscal/taxpayers/***")]
    [InlineData("/api/fiscal/taxpayers/001-1234567-8", "/api/fiscal/taxpayers/***")]
    [InlineData("/api/fiscal/taxpayers/00112345678/x", "/api/fiscal/taxpayers/***/x")]
    [InlineData("/api/sales/12345678", "/api/sales/12345678")] // 8 digits: not an RNC
    [InlineData("/api/sales/0190a1b2-0000-7000-8000-000000000000", "/api/sales/0190a1b2-0000-7000-8000-000000000000")]
    public void C10_request_logs_never_carry_an_RNC_or_a_cedula(string path, string logged) => RequestLogs.Mask(path).ShouldBe(logged);

    [Theory]
    [InlineData(null, HealthStatus.Healthy)] // no file: development, or the task has not run yet
    [InlineData("""{ "ProtectionStatus": 1 }""", HealthStatus.Healthy)]
    [InlineData("""{ "ProtectionStatus": 0 }""", HealthStatus.Degraded)]
    [InlineData("""{ "ProtectionStatus": null }""", HealthStatus.Degraded)] // Windows without BitLocker
    public void C2_the_health_check_says_when_the_system_drive_is_not_encrypted(string? json, HealthStatus status) =>
        DiskEncryptionHealthCheck.Evaluate(json).Status.ShouldBe(status);

    [Fact]
    public void C14_from_the_screen_a_copy_goes_to_a_local_drive() =>
        BackupSettings.CopyAllowed(Path.Combine(Path.GetTempPath(), "copia"), new ConfigurationBuilder().Build()).ShouldBeTrue();

    [Fact, Trait("Category", "Windows")]
    public void C14_a_network_folder_only_when_an_administrator_listed_it()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only: \\\\server\\share is a UNC path only on Windows (the Windows CI job runs it).");
        var none = new ConfigurationBuilder().Build();
        BackupSettings.CopyAllowed(@"\\intruso\respaldos", none).ShouldBeFalse();

        var listed = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backup:AllowedCopyFolders:0"] = @"\\nas\abastalia",
        }).Build();
        BackupSettings.CopyAllowed(@"\\nas\abastalia\tienda1", listed).ShouldBeTrue();
        BackupSettings.CopyAllowed(@"\\nas\abastaliaotra", listed).ShouldBeFalse();
    }
}

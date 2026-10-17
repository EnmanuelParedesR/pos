using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.Identity.Persistence;
using Abastalia.IntegrationTests.Infra;

namespace Abastalia.IntegrationTests.Settings;

/// <summary>R9 #3 / R12 (audit D): a backup from another store (other PIN pepper) where the installer's code is a Cajero: the installer keeps their
/// PIN and their role. Own app: it rewinds the users by hand.</summary>
public sealed class RestoreForeignUsersTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    [Fact]
    public async Task The_installer_takes_over_the_restored_user_with_the_same_code_and_keeps_the_Administrador_role()
    {
        var owner = await app.AdminAsync();
        await app.InScopeAsync(async sp =>
        {
            var snapshots = new List<RestoreSnapshot>();
            foreach (var guard in sp.GetServices<IRestoreGuard>()) snapshots.Add(await guard.CaptureAsync(CancellationToken.None));
            var identity = sp.GetRequiredService<IdentityDbContext>();
            var admin = await identity.Users.SingleAsync(u => u.Code == "ADMIN");
            await identity.Sessions.Where(s => s.UserId == admin.Id).ExecuteDeleteAsync();
            await identity.Users.Where(u => u.Id == admin.Id).ExecuteDeleteAsync();
            var restored = new Abastalia.Identity.Domain.User { Id = Guid.CreateVersion7(), Name = "Cajero de la otra tienda", Code = "ADMIN", RoleId = Roles.Cashier };
            restored.ChangePin("9999", RandomNumberGenerator.GetBytes(32)); // another server's pepper
            identity.Users.Add(restored);
            await identity.SaveChangesAsync();
            foreach (var s in snapshots) await s.ReapplyAsync(CancellationToken.None);
            return 0;
        });

        var signedIn = app.Client(await app.SignInAsync("admin", AbastaliaApp.AdminPin, owner.Device));
        (await signedIn.GetAsync("/api/identity/users")).StatusCode.ShouldBe(HttpStatusCode.OK); // users.manage: still the Administrador
        (await app.InScopeAsync(sp => sp.GetRequiredService<IdentityDbContext>().Users.Where(u => u.Code == "ADMIN").Select(u => u.RoleId).SingleAsync()))
            .ShouldBe(Roles.Administrator);
    }
}

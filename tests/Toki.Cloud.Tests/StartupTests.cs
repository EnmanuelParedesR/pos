using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;

namespace Toki.Cloud.Tests;

/// <summary>e2e 2026-10-16: in Development the host validates scopes, and a scoped service taken by a singleton (or by something resolved
/// once from the root, as MapIdentityApi does with its e-mail sender) stopped it from starting.</summary>
public class StartupTests
{
    private sealed class ScopeValidatingCloudApp : CloudApp
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseDefaultServiceProvider(o => o.ValidateScopes = true);
        }
    }

    [Fact]
    public async Task Starts_with_scope_validation_and_sends_the_register_email()
    {
        await using var app = new ScopeValidatingCloudApp();
        var r = await app.CreateClient().PostAsJsonAsync("/api/auth/register", new { email = "nuevo@example.com", password = CloudApp.Password });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }
}

using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Security;
using Toki.SharedKernel;

namespace Toki.IntegrationTests.Infra;

public static class AsUser
{
    /// <summary>Runs a command through the real pipeline as the given session's user and device (no extra permissions).</summary>
    internal static async Task<Result<Ok>> RunAsync<TCommand>(this TokiApp app, UserSession session, TCommand command) where TCommand : ICommand<Ok>
    {
        var me = await app.Client(session).GetFromJsonAsync<JsonElement>("/api/identity/me");
        var identity = new ClaimsIdentity(
        [
            new Claim(TokiClaims.User, session.UserId.ToString()),
            new Claim(TokiClaims.Device, me.GetProperty("deviceId").GetGuid().ToString()),
        ], "test");

        var accessor = app.Services.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        accessor.HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        try
        {
            await using var scope = app.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand, Ok>>().Handle(command, CancellationToken.None);
        }
        finally { accessor.HttpContext = previous; }
    }
}

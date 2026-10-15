using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Security;
using Toki.SharedKernel;

namespace Toki.IntegrationTests.Infra;

public static class ComoUsuario
{
    /// <summary>Runs a command through the real pipeline as the given session's user and device (no extra permissions).</summary>
    internal static async Task<Result<Ok>> EjecutarAsync<TCommand>(this TokiApp app, Sesion sesion, TCommand comando) where TCommand : ICommand<Ok>
    {
        var yo = await app.Cliente(sesion).GetFromJsonAsync<JsonElement>("/api/identidad/yo");
        var identidad = new ClaimsIdentity(
        [
            new Claim(TokiClaims.Usuario, sesion.UsuarioId.ToString()),
            new Claim(TokiClaims.Dispositivo, yo.GetProperty("dispositivoId").GetGuid().ToString()),
        ], "prueba");

        var accessor = app.Services.GetRequiredService<IHttpContextAccessor>();
        var anterior = accessor.HttpContext;
        accessor.HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identidad) };
        try
        {
            await using var scope = app.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand, Ok>>().Handle(comando, CancellationToken.None);
        }
        finally { accessor.HttpContext = anterior; }
    }
}

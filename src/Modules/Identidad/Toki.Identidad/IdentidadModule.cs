using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Toki.BuildingBlocks;
using Toki.BuildingBlocks.Security;
using Toki.Identidad.Domain;
using Toki.Identidad.Persistence;
using Toki.SharedKernel;

namespace Toki.Identidad;

public static class IdentidadModule
{
    public const string Esquema = "Toki";
    /// <summary>Header carrying the paired device's secret on every request.</summary>
    public const string HeaderDispositivo = "X-Dispositivo";

    public static IServiceCollection AddIdentidadModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<IdentidadDbContext>();
        services.AddScoped<IAutorizacionesSupervisor, AutorizacionesSupervisor>();
        services.AddAuthentication(Esquema).AddScheme<AuthenticationSchemeOptions, SesionAuthenticationHandler>(Esquema, null);
        services.AddAuthorization();
        return services;
    }
}

/// <summary>Authenticates "Authorization: Bearer &lt;session&gt;" plus the device header the session was opened on.</summary>
/// <remarks>shortcut: device identity is a bearer secret, not a client certificate; move to mTLS pairing before the pilot (TK-091).</remarks>
internal sealed class SesionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IdentidadDbContext db, TimeProvider reloj)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var auth = Request.Headers.Authorization.ToString();
        var dispositivo = Request.Headers[IdentidadModule.HeaderDispositivo].ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.Ordinal) || dispositivo.Length == 0) return AuthenticateResult.NoResult();

        var tokenHash = Token.Hash(auth["Bearer ".Length..].Trim());
        var dispositivoHash = Token.Hash(dispositivo);
        var ahora = reloj.GetUtcNow().UtcDateTime;

        var datos = await (
            from s in db.Sesiones
            join d in db.Dispositivos on s.DispositivoId equals d.Id
            join u in db.Usuarios on s.UsuarioId equals u.Id
            join r in db.Roles on u.RolId equals r.Id
            where s.TokenHash == tokenHash && s.CerradaUtc == null && s.ExpiraUtc > ahora
                  && d.TokenHash == dispositivoHash && !d.Revocado && u.Activo
            select new { SesionId = s.Id, UsuarioId = u.Id, u.Nombre, DispositivoId = d.Id, r.Permisos })
            .AsNoTracking().FirstOrDefaultAsync(Context.RequestAborted);
        if (datos is null) return AuthenticateResult.Fail("Sesión inválida o vencida.");

        List<Claim> claims =
        [
            new(TokiClaims.Usuario, datos.UsuarioId.ToString()),
            new(TokiClaims.Nombre, datos.Nombre),
            new(TokiClaims.Dispositivo, datos.DispositivoId.ToString()),
            new(TokiClaims.Sesion, datos.SesionId.ToString()),
            .. datos.Permisos.Select(p => new Claim(TokiClaims.Permiso, p)),
        ];
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name));
    }
}

internal sealed class AutorizacionesSupervisor(IdentidadDbContext db, IUserContext usuario, TimeProvider reloj) : IAutorizacionesSupervisor
{
    public async Task<Result<Guid>> ConsumirAsync(Guid autorizacionId, string permiso, Guid recursoId, CancellationToken ct)
    {
        var ahora = reloj.GetUtcNow().UtcDateTime;
        var vigente = db.Autorizaciones.Where(a => a.Id == autorizacionId && a.UsadaUtc == null && a.ExpiraUtc > ahora
            && a.Permiso == permiso && a.RecursoId == recursoId && a.DispositivoId == usuario.DispositivoId);
        // Conditional update: only one command can consume it, even when two race.
        if (await vigente.ExecuteUpdateAsync(s => s.SetProperty(a => a.UsadaUtc, ahora), ct) == 0)
            return IdentidadErrors.AutorizacionInvalida;
        return await db.Autorizaciones.Where(a => a.Id == autorizacionId).Select(a => a.SupervisorId).SingleAsync(ct);
    }
}

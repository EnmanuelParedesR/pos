using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Abastalia.Host.Cloud.Accounts;
using Abastalia.Host.Cloud.Fe;
using Abastalia.Host.Cloud.Nodes;
using Abastalia.Host.Cloud.Persistence;
using Abastalia.SharedKernel;

namespace Abastalia.Host.Cloud.Assistant;

/// <summary>TK-341: phase 1, the panel's own writes (docs/talia-acciones.md §1, Owner only). Each calls the same code as its endpoint; the summary is
/// written here from the database, and the arguments the model sent are replaced by the ids they resolved to.</summary>
internal abstract class OwnerAction : AssistantActionType
{
    public override IReadOnlyList<string> Policies { get; } = [CloudAuth.PanelPolicy, CloudAuth.OwnerPolicy];

    protected static JsonObject Schema(params (string Name, JsonObject Spec)[] properties) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(properties.Select(p => KeyValuePair.Create(p.Name, (JsonNode?)p.Spec))),
        ["required"] = new JsonArray([.. properties.Select(p => (JsonNode)p.Name)]),
        ["additionalProperties"] = false,
    };

    protected static JsonObject Text(int max, string description) => new() { ["type"] = "string", ["maxLength"] = max, ["description"] = description };
    protected static JsonObject Flag(string description) => new() { ["type"] = "boolean", ["description"] = description };
    protected static JsonObject Role() => new()
    {
        ["type"] = "string", ["enum"] = new JsonArray("Owner", "ReadOnly"), ["description"] = "Owner: Dueño (hace todo); ReadOnly: Solo lectura (solo mira).",
    };
    protected static JsonObject Email() => Text(256, "Correo de la persona, tal como lo dijo el usuario.");

    protected static string Label(MemberRole role) => role == MemberRole.Owner ? "Dueño" : "Solo lectura";
    protected static string S(JsonObject args, string name) => args[name]?.GetValue<string>() ?? "";
    protected static DateTime Now(ActionContext ctx) => ctx.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
    protected static Result<object?> Done(Result r, string shown) => r.IsSuccess ? Result.Success<object?>(shown) : r.Error!;

    /// <summary>A member of this tenant by e-mail.</summary>
    protected static Task<Member?> MemberAsync(ActionContext ctx, string email)
    {
        var normalized = email.Trim().ToUpperInvariant();
        return ctx.Db.TenantMembers.AsNoTracking().Where(m => m.TenantId == ctx.TenantId)
            .Join(ctx.Db.Users, m => m.UserId, u => u.Id, (m, u) => new { m, u.NormalizedEmail, u.Email })
            .Where(x => x.NormalizedEmail == normalized).Select(x => new Member(x.m.UserId, x.Email ?? email, x.m.Role, x.m.CanViewCosts))
            .SingleOrDefaultAsync();
    }

    protected sealed record Member(string UserId, string Email, MemberRole Role, bool CanViewCosts);

    /// <summary>E8 (§2): nobody changes their own role or permissions through Talia, checked when proposed and again when confirmed.</summary>
    protected static readonly Error OwnAccess = Error.Forbidden("Assistant.OwnAccess",
        "Su propio rol y sus permisos no los cambio yo. Si hace falta, otro dueño lo hace en Equipo (/app/members).");

    protected static readonly Error NotAMember = Error.NotFound("Members.NotFound", "No hay nadie con ese correo en el equipo del panel (/app/members).");
}

internal sealed class InviteMemberAction : OwnerAction
{
    public override string Type => "invite_member";
    public override string Description => "Propone invitar a una persona al panel de este negocio con un rol. Le llega un enlace por correo (7 días).";
    public override JsonObject Parameters { get; } = Schema(("email", Email()), ("role", Role()));

    public override async Task<Result<(JsonObject Arguments, string Summary)>> PrepareAsync(ActionContext ctx, JsonObject args, CancellationToken ct)
    {
        var (email, role) = (S(args, "email").Trim(), Enum.Parse<MemberRole>(S(args, "role")));
        if (MemberEndpoints.InviteError(email, role) is { } invalid) return invalid;
        // Without e-mail the invite link is shown once to whoever invites; it is a secret, so it is never kept in a proposal's result.
        if (!ctx.Services.GetRequiredService<IEmailSender>().Delivers)
            return Error.Conflict("Assistant.InviteNeedsEmail", "Aquí las invitaciones no salen por correo: invítela desde Equipo (/app/members) para copiar el enlace.");
        if (await MemberEndpoints.IsMemberAsync(ctx.Db, ctx.TenantId, email, ct)) return MemberEndpoints.AlreadyMemberError;
        return (new JsonObject { ["email"] = email, ["role"] = role.ToString() },
            $"Invitar a {email} al panel como {Label(role)} (le llega un enlace por correo que vale 7 días)");
    }

    public override async Task<Result<object?>> ExecuteAsync(ActionContext ctx, JsonObject args, CancellationToken ct)
    {
        var s = ctx.Services;
        var (result, _) = await MemberEndpoints.InviteAsync(ctx.User, ctx.Db, s.GetRequiredService<IEmailSender>(), s.GetRequiredService<IConfiguration>(),
            s.GetRequiredService<TimeProvider>(), s.GetRequiredService<Quotas.IQuota>(), S(args, "email"), Enum.Parse<MemberRole>(S(args, "role")), ct);
        if (result.IsFailure) return result.Error!;
        if (result.Value.Link is not null) return Error.Conflict("Assistant.InviteNeedsEmail", "La invitación no salió por correo."); // rolls back
        return $"Invitación enviada a {result.Value.Invite.Email}.";
    }
}

internal sealed class CancelInviteAction : OwnerAction
{
    public override string Type => "cancel_invite";
    public override string Description => "Propone cancelar la invitación pendiente al panel de una persona (por su correo).";
    public override JsonObject Parameters { get; } = Schema(("email", Email()));

    public override async Task<Result<(JsonObject Arguments, string Summary)>> PrepareAsync(ActionContext ctx, JsonObject args, CancellationToken ct)
    {
        var email = S(args, "email").Trim();
        var pending = (await MemberEndpoints.PendingInvites(ctx.Db, ctx.TenantId, Now(ctx)).ToListAsync(ct))
            .Where(i => string.Equals(i.Email, email, StringComparison.OrdinalIgnoreCase)).ToList();
        if (pending is not [var invite])
            return pending.Count == 0
                ? Error.NotFound("Members.InviteNotFound", $"No hay una invitación pendiente para {email}.")
                : Error.Conflict("Assistant.Ambiguous", $"Hay {pending.Count} invitaciones pendientes para {email}: cancélelas en Equipo (/app/members).");
        return (new JsonObject { ["inviteId"] = invite.Id.ToString() },
            $"Cancelar la invitación de {invite.Email} ({Label(invite.Role)}, enviada el {invite.CreatedUtc.AddHours(-4):dd/MM/yyyy})");
    }

    public override async Task<Result<object?>> ExecuteAsync(ActionContext ctx, JsonObject args, CancellationToken ct) =>
        Done(await MemberEndpoints.CancelInviteAsync(ctx.Db, ctx.TenantId, Guid.Parse(S(args, "inviteId")), Now(ctx), ct), "Invitación cancelada.");
}

internal sealed class MemberRoleAction : OwnerAction
{
    public override string Type => "member_role";
    public override string Description =>
        "Propone cambiar el rol en el panel de OTRO miembro del equipo (por su correo). Nunca el de la persona que pregunta.";
    public override JsonObject Parameters { get; } = Schema(("email", Email()), ("role", Role()));
    // The "another Owner remains" check of MemberEndpoints.ChangeRoleAsync needs it (two Owners demoting each other at once).
    public override System.Data.IsolationLevel? Isolation => System.Data.IsolationLevel.Serializable;

    public override async Task<Result<(JsonObject Arguments, string Summary)>> PrepareAsync(ActionContext ctx, JsonObject args, CancellationToken ct)
    {
        var role = Enum.Parse<MemberRole>(S(args, "role"));
        if (await MemberAsync(ctx, S(args, "email")) is not { } m) return NotAMember;
        if (m.UserId == ctx.UserId) return OwnAccess;
        if (m.Role == role) return Error.Conflict("Assistant.NothingToChange", $"{m.Email} ya es {Label(role)}: no hace falta cambiarlo.");
        return (new JsonObject { ["userId"] = m.UserId, ["role"] = role.ToString() }, $"Rol de {m.Email} en el panel: {Label(m.Role)} → {Label(role)}");
    }

    public override async Task<Result<object?>> ExecuteAsync(ActionContext ctx, JsonObject args, CancellationToken ct)
    {
        var userId = S(args, "userId");
        if (userId == ctx.UserId) return OwnAccess;
        return Done(await MemberEndpoints.ChangeRoleAsync(ctx.Db, ctx.TenantId, userId, Enum.Parse<MemberRole>(S(args, "role")), ct), "Rol cambiado.");
    }
}

internal sealed class MemberCostsAction : OwnerAction
{
    public override string Type => "member_costs";
    public override string Description =>
        "Propone dar o quitar a OTRO miembro de Solo lectura el permiso «Ver costos» (costos y ganancias). Nunca a la persona que pregunta.";
    public override JsonObject Parameters { get; } = Schema(("email", Email()), ("canViewCosts", Flag("true para que vea costos y ganancias, false para quitárselo.")));

    public override async Task<Result<(JsonObject Arguments, string Summary)>> PrepareAsync(ActionContext ctx, JsonObject args, CancellationToken ct)
    {
        var allowed = args["canViewCosts"]!.GetValue<bool>();
        if (await MemberAsync(ctx, S(args, "email")) is not { } m) return NotAMember;
        if (m.UserId == ctx.UserId) return OwnAccess;
        if (m.Role == MemberRole.Owner) return Error.Conflict("Assistant.NothingToChange", $"{m.Email} es Dueño: los dueños siempre ven costos.");
        if (m.CanViewCosts == allowed) return Error.Conflict("Assistant.NothingToChange", $"{m.Email} ya {(allowed ? "ve" : "no ve")} costos: no hace falta cambiarlo.");
        return (new JsonObject { ["userId"] = m.UserId, ["canViewCosts"] = allowed },
            $"«Ver costos» de {m.Email} (Solo lectura): {(m.CanViewCosts ? "sí" : "no")} → {(allowed ? "sí" : "no")}");
    }

    public override async Task<Result<object?>> ExecuteAsync(ActionContext ctx, JsonObject args, CancellationToken ct)
    {
        var userId = S(args, "userId");
        if (userId == ctx.UserId) return OwnAccess;
        return Done(await MemberEndpoints.ChangeCostsAsync(ctx.Db, ctx.TenantId, userId, args["canViewCosts"]!.GetValue<bool>(), ct), "Permiso cambiado.");
    }
}

internal sealed class CreateStoreAction : OwnerAction
{
    public override string Type => "create_store";
    public override string Description => "Propone crear una tienda (sucursal) nueva en este negocio, con su nombre.";
    public override JsonObject Parameters { get; } = Schema(("name", Text(100, "Nombre de la tienda nueva.")));

    public override async Task<Result<(JsonObject Arguments, string Summary)>> PrepareAsync(ActionContext ctx, JsonObject args, CancellationToken ct)
    {
        var name = S(args, "name").Trim();
        if (name.Length == 0) return Error.Validation("Stores.InvalidName", "Falta el nombre de la tienda.");
        var names = await ctx.Db.Stores.AsNoTracking().Where(s => s.TenantId == ctx.TenantId).Select(s => s.Name).ToListAsync(ct);
        var twin = names.Any(n => string.Equals(n.Trim(), name, StringComparison.OrdinalIgnoreCase));
        return (new JsonObject { ["name"] = name }, $"Crear la tienda «{name}»" + (twin ? " (ya hay una tienda con ese nombre)" : ""));
    }

    public override async Task<Result<object?>> ExecuteAsync(ActionContext ctx, JsonObject args, CancellationToken ct)
    {
        var store = await StoreEndpoints.CreateAsync(ctx.Db, ctx.TenantId, S(args, "name"), Now(ctx), ct);
        return store.IsSuccess ? $"Tienda «{store.Value.Name}» creada. Para conectar su PC, pídame un código de conexión." : store.Error!;
    }
}

internal sealed class ConnectionCodeAction : OwnerAction
{
    public override string Type => "connection_code";
    public override string Description =>
        "Propone sacar un código de conexión (8 dígitos, 30 minutos, un solo uso) para conectar la PC principal de una tienda a la nube. " +
        "El código lo ve solo la persona en la tarjeta; usted nunca lo ve.";
    public override JsonObject Parameters { get; } = Schema(("store", Text(100, "Nombre de la tienda o su storeId.")));

    public override async Task<Result<(JsonObject Arguments, string Summary)>> PrepareAsync(ActionContext ctx, JsonObject args, CancellationToken ct)
    {
        var wanted = S(args, "store").Trim();
        var stores = await ctx.Db.Stores.AsNoTracking().Where(s => s.TenantId == ctx.TenantId).Select(s => new { s.Id, s.Name }).ToListAsync(ct);
        var match = stores.Where(s => string.Equals(s.Id.ToString(), wanted, StringComparison.OrdinalIgnoreCase) || string.Equals(s.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (match.Count == 0) match = [.. stores.Where(s => s.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))];
        // An empty name matches every store: fine with one, a question with more.
        if (match is not [var store])
            return match.Count == 0 ? Error.NotFound("Stores.NotFound", $"No encontré una tienda «{wanted}» en este negocio.")
                : Error.Conflict("Assistant.Ambiguous", $"Hay {match.Count} tiendas que coinciden: pregúntele al usuario para cuál.");
        return (new JsonObject { ["storeId"] = store.Id.ToString() },
            $"Código de conexión para la tienda «{store.Name}» (vale 30 minutos y sirve una sola vez)");
    }

    public override async Task<Result<object?>> ExecuteAsync(ActionContext ctx, JsonObject args, CancellationToken ct)
    {
        var code = await StoreEndpoints.NewCodeAsync(ctx.Db, ctx.TenantId, Guid.Parse(S(args, "storeId")), Now(ctx), ct);
        // A plain string: the card shows it (ProposalCard); it is never sent back to the model.
        return code.IsSuccess ? $"Código: {code.Value.Code}. Escríbalo en la PC principal de la tienda antes de 30 minutos." : code.Error!;
    }
}

internal sealed class ReceivedDecisionAction : OwnerAction
{
    public override string Type => "received_decision";
    public override string Description =>
        "Propone aceptar o rechazar (aprobación comercial, ACECF) un e-CF que un suplidor le envió y que todavía no tiene decisión. " +
        "Rechazar pide el motivo que dijo el usuario. La PC de la tienda lo firma y lo envía después.";
    public override JsonObject Parameters { get; } = Optional(Schema(
        ("document", Text(60, "El e-NCF (por ejemplo E310000000045) o el nombre del suplidor.")),
        ("approve", Flag("true para aceptarlo, false para rechazarlo.")),
        ("reason", Text(250, "Motivo del rechazo, con las palabras del usuario (solo al rechazar)."))), "reason");

    private static JsonObject Optional(JsonObject schema, string name)
    {
        var required = schema["required"]!.AsArray();
        required.Remove(required.First(r => r!.GetValue<string>() == name));
        return schema;
    }

    public override async Task<Result<(JsonObject Arguments, string Summary)>> PrepareAsync(ActionContext ctx, JsonObject args, CancellationToken ct)
    {
        var (wanted, approve, reason) = (S(args, "document").Trim(), args["approve"]!.GetValue<bool>(), S(args, "reason").Trim());
        if (!approve && reason.Length == 0) return Error.Validation("Fe.ReasonRequired", "Para rechazarlo hace falta el motivo: pregúnteselo al usuario.");
        if (wanted.Length == 0) return Error.Validation("Assistant.Ambiguous", "Diga qué comprobante (su e-NCF o el suplidor).");
        var pending = await ctx.Db.FeReceived.AsNoTracking()
            .Where(d => d.TenantId == ctx.TenantId && d.Approval == null && d.PanelDecision == null && !d.SignerUnverified)
            .Select(d => new { d.Id, d.ENcf, d.IssuerName, d.Total, d.IssuedOn }).ToListAsync(ct);
        var match = pending.Where(d => string.Equals(d.ENcf, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (match.Count == 0) match = [.. pending.Where(d => d.IssuerName.Contains(wanted, StringComparison.OrdinalIgnoreCase))];
        if (match is not [var doc])
            return match.Count == 0
                ? Error.NotFound("Fe.NotFound", $"No hay un comprobante recibido sin decidir con «{wanted}». Véalos en /app/received.")
                : Error.Conflict("Assistant.Ambiguous", $"Hay {match.Count} comprobantes de ese suplidor sin decidir: pídale al usuario el e-NCF.");
        if (!await ctx.Db.FeCertificates.AnyAsync(c => c.TenantId == ctx.TenantId && c.RevokedUtc == null, ct)) return FeErrors.ReceiverInactive;
        var supplier = doc.IssuerName.Length > 80 ? doc.IssuerName[..80] + "…" : doc.IssuerName; // from the supplier's XML: data, kept short
        var what = $"e-CF {doc.ENcf} de {supplier} (RD$ {doc.Total.ToString("N2", CultureInfo.InvariantCulture)}, {doc.IssuedOn:dd/MM/yyyy})";
        var resolved = new JsonObject { ["id"] = doc.Id, ["approve"] = approve };
        if (!approve) resolved["reason"] = reason;
        return (resolved, approve ? $"Aceptar el {what}" : $"Rechazar el {what}. Motivo: «{reason}»");
    }

    public override async Task<Result<object?>> ExecuteAsync(ActionContext ctx, JsonObject args, CancellationToken ct)
    {
        var approve = args["approve"]!.GetValue<bool>();
        var r = await FeServices.DecideAsync(ctx.Db, ctx.User, args["id"]!.GetValue<long>(), approve, args["reason"]?.GetValue<string>(), Now(ctx),
            ctx.Services.GetRequiredService<ILoggerFactory>(), ct);
        return r.IsSuccess ? $"{(approve ? "Aceptado" : "Rechazado")}. La PC de la tienda lo firma y lo envía al suplidor cuando se conecte." : r.Error!;
    }
}

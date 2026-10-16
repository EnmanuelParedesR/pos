using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.Host.Cloud.Accounts;
using Toki.Host.Cloud.Nodes;
using Toki.Host.Cloud.Persistence;
using Toki.Host.Cloud.Platform;
using Toki.SharedKernel;

namespace Toki.Host.Cloud.Certification;

/// <summary>TK-234: where an accompaniment request is (docs/servicio-certificacion.md §4). Certified and Cancelled close it.</summary>
public enum AccompanimentStatus { New, Scheduled, Session1Done, Session2Done, Certified, Cancelled }

/// <summary>TK-233: the owner asked Toki staff to walk them through the DGII certification (docs/servicio-certificacion.md).</summary>
public sealed class AccompanimentRequest
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid StoreId { get; set; }
    public string ContactName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string PreferredTimes { get; set; } = "";
    public AccompanimentStatus Status { get; set; }
    public DateTime? Session1Utc { get; set; }
    public DateTime? Session2Utc { get; set; }
    /// <summary>Staff only: never goes back to the store.</summary>
    public string? Notes { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ChangedUtc { get; set; }

    public bool Open => Status is not (AccompanimentStatus.Certified or AccompanimentStatus.Cancelled);
}

public sealed record AccompanimentAsk(string? ContactName, string? Phone, string? PreferredTimes);

/// <summary>What the store sees: no staff notes.</summary>
public sealed record AccompanimentForStore(Guid Id, AccompanimentStatus Status, string ContactName, string Phone, string PreferredTimes, DateTime? Session1Utc,
    DateTime? Session2Utc, DateTime CreatedUtc);

public sealed record AccompanimentState(AccompanimentForStore? Request);

public sealed record PlatformAccompaniment(Guid Id, Guid TenantId, string TenantName, Guid StoreId, string StoreName, string ContactName, string Phone,
    string PreferredTimes, AccompanimentStatus Status, DateTime? Session1Utc, DateTime? Session2Utc, string? Notes, DateTime CreatedUtc, DateTime ChangedUtc);

public sealed record AccompanimentUpdate(AccompanimentStatus? Status, DateTime? Session1Utc, DateTime? Session2Utc, string? Notes);

/// <summary>TK-235: what the owner prepares before session 1 (docs/servicio-certificacion.md §5). The site's /certificacion shows the same list.</summary>
public static class AccompanimentChecklist
{
    public static readonly string[] Items =
    [
        "RNC activo y al día con la DGII (sin declaraciones pendientes).",
        "Acceso a la Oficina Virtual (OFV) de la empresa y, si aplica, del representante: usuario y clave que solo usted conoce. Pruebe que entra antes de la cita. Toki nunca le pedirá esa clave.",
        "Alta de NCF (autorización para emitir comprobantes fiscales).",
        "Un representante vinculado al RNC, que será el Usuario Administrador e-CF.",
        "El certificado digital de persona física para procedimientos tributarios, a nombre de ese representante, comprado a Avansi (Viafirma) o a la Cámara de Comercio y Producción de Santo Domingo (Digifirma). Pídalo como archivo .p12 o .pfx con su contraseña (no en token USB ni en la nube del emisor).",
        "La PC principal de la tienda con Toki instalado, encendida y con internet durante la cita.",
        "Una hora libre para la cita, sin atender el mostrador.",
    ];
}

internal static class AccompanimentModel
{
    public static void Configure(ModelBuilder builder) =>
        builder.Entity<AccompanimentRequest>(e =>
        {
            e.ToTable("AccompanimentRequests");
            e.HasKey(r => r.Id);
            e.Property(r => r.ContactName).HasMaxLength(100);
            e.Property(r => r.Phone).HasMaxLength(30);
            e.Property(r => r.PreferredTimes).HasMaxLength(200);
            e.Property(r => r.Notes).HasMaxLength(2000);
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(r => new { r.TenantId, r.StoreId });
            // R10 #12: one open request per store, even with two taps at once.
            e.HasIndex(r => new { r.TenantId, r.StoreId }, "IX_AccompanimentRequests_OneOpen").IsUnique()
                .HasFilter("[Status] <> 'Certified' AND [Status] <> 'Cancelled'");
            e.Ignore(r => r.Open);
        });
}

/// <summary>TK-233 (the store asks, staff get an e-mail) and TK-234 (staff schedule the two sessions in the console; the owners get the confirmation).</summary>
internal static class AccompanimentEndpoints
{
    private static readonly Error Invalid = Error.Validation("Accompaniment.Invalid",
        "Escriba el nombre de contacto y un teléfono (7 a 30 caracteres). El horario preferido es opcional (hasta 200).");

    public static void MapAccompaniment(this IEndpointRouteBuilder app)
    {
        var store = app.MapGroup("/fiscal/v1/accompaniment").RequireAuthorization(CloudAuth.NodePolicy).WithTags("Accompaniment");

        store.MapPost("/status", async (ClaimsPrincipal user, CloudDbContext db, CancellationToken ct) =>
        {
            var node = NodeIdentity.From(user);
            return Results.Ok(new AccompanimentState(ForStore(await LatestAsync(db, node, ct))));
        });

        // An open request is answered as it is (a second tap sends nothing again); after Certified or Cancelled a new one can start.
        store.MapPost("", async (AccompanimentAsk r, ClaimsPrincipal user, CloudDbContext db, IEmailSender email, IConfiguration config,
            TimeProvider clock, ILogger<AccompanimentRequest> log, CancellationToken ct) =>
        {
            var (name, phone, times) = (r.ContactName?.Trim() ?? "", r.Phone?.Trim() ?? "", r.PreferredTimes?.Trim() ?? "");
            if (name.Length is 0 or > 100 || phone.Length is < 7 or > 30 || times.Length > 200) return EndpointExtensions.Problem(Invalid);
            var node = NodeIdentity.From(user);
            if (await LatestAsync(db, node, ct) is { Open: true } open) return Results.Ok(new AccompanimentState(ForStore(open)));

            var now = clock.GetUtcNow().UtcDateTime;
            var request = new AccompanimentRequest
            {
                Id = Guid.CreateVersion7(), TenantId = node.TenantId, StoreId = node.StoreId, ContactName = name, Phone = phone, PreferredTimes = times,
                Status = AccompanimentStatus.New, CreatedUtc = now, ChangedUtc = now,
            };
            db.AccompanimentRequests.Add(request);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                db.Entry(request).State = EntityState.Detached;
                if (await LatestAsync(db, node, ct) is not { Open: true } other) throw;
                return Results.Ok(new AccompanimentState(ForStore(other))); // the other tap won
            }
            await NotifyStaffAsync(db, email, config, log, request, ct);
            return Results.Ok(new AccompanimentState(ForStore(request)));
        });
    }

    /// <summary>Toki staff: mapped inside the audited /api/platform group (every call leaves a PlatformAudit row).</summary>
    public static void MapPlatformAccompaniment(this RouteGroupBuilder group)
    {
        var platform = group.MapGroup("/accompaniment");

        platform.MapGet("", async (CloudDbContext db, CancellationToken ct) =>
        {
            var rows = await db.AccompanimentRequests.AsNoTracking().OrderByDescending(r => r.CreatedUtc).Take(500).ToListAsync(ct);
            // Open ones first, newest first inside each half.
            return Results.Ok(await ForPlatformAsync(db, [.. rows.OrderBy(r => r.Open ? 0 : 1).ThenByDescending(r => r.CreatedUtc)], ct));
        });

        platform.MapPut("/{id:guid}", async (Guid id, AccompanimentUpdate u, CloudDbContext db, IEmailSender email, TimeProvider clock,
            ILogger<AccompanimentRequest> log, CancellationToken ct) =>
        {
            if (u.Status is null || !Enum.IsDefined(u.Status.Value) || u.Notes?.Length > 2000)
                return EndpointExtensions.Problem(Error.Validation("Accompaniment.InvalidUpdate", "Elija un estado válido; las notas van hasta 2000 caracteres."));
            var r = await db.AccompanimentRequests.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (r is null) return EndpointExtensions.Problem(Error.NotFound("Accompaniment.NotFound", "No encontramos esa solicitud."));

            // R10 #12: a date left out keeps the one scheduled; a new one must be ahead, and session 2 after session 1.
            var now = clock.GetUtcNow().UtcDateTime;
            var (newSession1, newSession2) = (u.Session1Utc is { } s1 && s1 != r.Session1Utc, u.Session2Utc is { } s2 && s2 != r.Session2Utc);
            var (session1, session2) = (u.Session1Utc ?? r.Session1Utc, u.Session2Utc ?? r.Session2Utc);
            if ((newSession1 && session1 <= now) || (newSession2 && session2 <= now) || (session2 is not null && (session1 is null || session2 <= session1)))
                return EndpointExtensions.Problem(Error.Validation("Accompaniment.InvalidDates", "Las citas van en el futuro, y la segunda después de la primera."));
            (r.Status, r.Session1Utc, r.Session2Utc, r.Notes, r.ChangedUtc) =
                (u.Status.Value, session1, session2, string.IsNullOrWhiteSpace(u.Notes) ? null : u.Notes.Trim(), now);
            await db.SaveChangesAsync(ct);
            if (newSession1) await NotifyOwnersAsync(db, email, log, r, 1, r.Session1Utc!.Value, ct);
            if (newSession2) await NotifyOwnersAsync(db, email, log, r, 2, r.Session2Utc!.Value, ct);
            return Results.Ok((await ForPlatformAsync(db, [r], ct))[0]);
        });
    }

    private static Task<AccompanimentRequest?> LatestAsync(CloudDbContext db, NodeIdentity node, CancellationToken ct) =>
        db.AccompanimentRequests.Where(r => r.TenantId == node.TenantId && r.StoreId == node.StoreId).OrderByDescending(r => r.CreatedUtc).FirstOrDefaultAsync(ct);

    private static AccompanimentForStore? ForStore(AccompanimentRequest? r) => r is null ? null
        : new(r.Id, r.Status, r.ContactName, r.Phone, r.PreferredTimes, r.Session1Utc, r.Session2Utc, r.CreatedUtc);

    private static async Task<List<PlatformAccompaniment>> ForPlatformAsync(CloudDbContext db, List<AccompanimentRequest> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.StoreId).Distinct().ToList();
        var stores = await db.Stores.AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var tenants = await TenantNamesAsync(db, [.. rows.Select(r => r.TenantId).Distinct()], ct);
        return [.. rows.Select(r => new PlatformAccompaniment(r.Id, r.TenantId, tenants.GetValueOrDefault(r.TenantId, ""), r.StoreId, stores.GetValueOrDefault(r.StoreId, ""),
            r.ContactName, r.Phone, r.PreferredTimes, r.Status, r.Session1Utc, r.Session2Utc, r.Notes, r.CreatedUtc, r.ChangedUtc))];
    }

    /// <summary>The owner's e-mail where there is one (as the console lists tenants), else the tenant name.</summary>
    private static async Task<Dictionary<Guid, string>> TenantNamesAsync(CloudDbContext db, List<Guid> ids, CancellationToken ct)
    {
        var names = await db.Tenants.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        foreach (var o in await OwnerEmailsAsync(db, ids, ct)) names[o.TenantId] = o.Email;
        return names;
    }

    private static async Task<List<(Guid TenantId, string Email)>> OwnerEmailsAsync(CloudDbContext db, List<Guid> tenants, CancellationToken ct) =>
        [.. (await db.TenantMembers.AsNoTracking().Where(m => tenants.Contains(m.TenantId) && m.Role == MemberRole.Owner)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { m.TenantId, u.Email }).Where(x => x.Email != null).ToListAsync(ct))
            .Select(x => (x.TenantId, x.Email!))];

    /// <summary><c>Certification:StaffEmails</c>; empty = only the log (nobody to tell yet).</summary>
    private static async Task NotifyStaffAsync(CloudDbContext db, IEmailSender email, IConfiguration config, ILogger log, AccompanimentRequest r, CancellationToken ct)
    {
        var staff = config.GetSection("Certification:StaffEmails").Get<string[]>()?.Where(e => !string.IsNullOrWhiteSpace(e)).ToArray() ?? [];
        var storeName = await db.Stores.AsNoTracking().Where(s => s.Id == r.StoreId).Select(s => s.Name).SingleOrDefaultAsync(ct) ?? "";
        log.LogInformation("Accompaniment requested by store {StoreId} of tenant {TenantId}; e-mailing {Count} staff", r.StoreId, r.TenantId, staff.Length);
        var owner = (await OwnerEmailsAsync(db, [r.TenantId], ct)).Select(o => o.Email).FirstOrDefault() ?? "sin correo";
        var body = $"La tienda {storeName} ({owner}) pidió acompañamiento en la certificación e-CF.\n\n" +
                   $"Contacto: {r.ContactName}\nTeléfono: {r.Phone}\nHorario preferido: {(r.PreferredTimes.Length > 0 ? r.PreferredTimes : "sin preferencia")}\n\n" +
                   $"Agéndela en {MemberEndpoints.PanelUrl(config)}/app/platform/accompaniment y siga docs/guia-tecnico-certificacion.md.";
        foreach (var to in staff)
        {
            try { await email.SendAsync(to, $"Toki · Solicitud de acompañamiento: {storeName}", body, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Accompaniment request could not be e-mailed to staff"); }
        }
    }

    /// <summary>The confirmation of a session to every owner of the tenant. Session 1 carries the preparation list (TK-235).</summary>
    private static async Task NotifyOwnersAsync(CloudDbContext db, IEmailSender email, ILogger log, AccompanimentRequest r, int session, DateTime atUtc, CancellationToken ct)
    {
        var storeName = await db.Stores.AsNoTracking().Where(s => s.Id == r.StoreId).Select(s => s.Name).SingleOrDefaultAsync(ct) ?? "";
        var body = $"Su cita {session} de acompañamiento en la certificación e-CF quedó para el {StoreTime(atUtc)} (hora de la tienda).\n\n" +
                   "Es una sesión remota con un técnico de Toki. Cuando haga falta escribir su clave de la Oficina Virtual de la DGII, usted la escribe y el " +
                   "técnico deja de ver su pantalla hasta que usted termine. Toki nunca le pedirá esa clave por WhatsApp, correo ni llamada.\n\n" +
                   (session == 1
                       ? "Antes de la cita, tenga listo:\n" + string.Concat(AccompanimentChecklist.Items.Select(i => $"- {i}\n"))
                       : "En esta cita se registran las URL de producción, usted acepta la declaración jurada, se piden las secuencias e-NCF y la tienda pasa " +
                         "a producción. Tenga a mano su acceso a la OFV y la PC principal encendida.\n") +
                   $"\nContacto que nos dio: {r.ContactName}, {r.Phone}. Si necesita cambiar la cita, respóndanos por WhatsApp.";
        foreach (var (_, to) in await OwnerEmailsAsync(db, [r.TenantId], ct))
        {
            try { await email.SendAsync(to, $"Toki · {storeName}: cita {session} de la certificación e-CF", body, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Session {Session} confirmation could not be e-mailed", session); }
        }
    }

    /// <summary>Store time (UTC−4, no daylight saving in the Dominican Republic).</summary>
    public static string StoreTime(DateTime utc) => utc.AddHours(-4).ToString("dd/MM/yyyy 'a las' h:mm tt", CultureInfo.InvariantCulture);
}

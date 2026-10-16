using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.Host.Cloud.Accounts;
using Abastalia.Host.Cloud.Nodes;
using Abastalia.Host.Cloud.Persistence;
using Abastalia.SharedKernel;

namespace Abastalia.Host.Cloud.Alerts;

/// <summary>TK-222: what the cloud warns a member about outside the app.</summary>
public enum AlertKind { DgiiRejected, CertificateExpiring, QueueStale, CertificationProgress }

/// <summary>A member's choice of alerts. No row yet = the default of the role (<see cref="For"/>).</summary>
public sealed class AlertPreference
{
    public Guid TenantId { get; set; }
    public string UserId { get; set; } = "";
    public bool DgiiRejected { get; set; }
    public bool CertificateExpiring { get; set; }
    public bool QueueStale { get; set; }
    public bool CertificationProgress { get; set; }
    /// <summary>The member used the link in an e-mail: nothing goes out until they turn something on again in the panel.</summary>
    public bool Unsubscribed { get; set; }
    /// <summary>The e-mail's unsubscribe link (256 random bits).</summary>
    /// <remarks>shortcut: kept as is, not hashed: every alert needs it again, and all it can do is stop that member's alerts.</remarks>
    public string UnsubscribeToken { get; set; } = "";

    /// <summary>Owners get every alert; a read-only member (an accountant) none until they choose.</summary>
    public static AlertPreference For(Guid tenantId, string userId, MemberRole role) => new()
    {
        TenantId = tenantId, UserId = userId, UnsubscribeToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
        DgiiRejected = role == MemberRole.Owner, CertificateExpiring = role == MemberRole.Owner, QueueStale = role == MemberRole.Owner,
        CertificationProgress = role == MemberRole.Owner,
    };

    public bool Wants(AlertKind kind) => !Unsubscribed && kind switch
    {
        AlertKind.DgiiRejected => DgiiRejected,
        AlertKind.CertificateExpiring => CertificateExpiring,
        AlertKind.QueueStale => QueueStale,
        _ => CertificationProgress,
    };
}

/// <summary>One alert per condition, store and store day: the dedupe (a second batch the same day sends nothing again).</summary>
public sealed class SentAlert
{
    public Guid TenantId { get; set; }
    public Guid StoreId { get; set; }
    public AlertKind Kind { get; set; }
    public DateOnly Day { get; set; }
    public DateTime SentUtc { get; set; }
    public int Recipients { get; set; }
}

/// <summary>WhatsApp alerts, a paid add-on later.</summary>
/// <remarks>shortcut: interface only. Wire a provider (Meta Cloud API or Twilio), a phone per member and a preference flag when the add-on sells.</remarks>
public interface IWhatsAppSender
{
    Task SendAsync(string phone, string text, CancellationToken ct);
}

public sealed record AlertPreferences(string? Email, bool DgiiRejected, bool CertificateExpiring, bool QueueStale, bool CertificationProgress, bool Unsubscribed,
    bool EmailDelivers);
public sealed record AlertPreferencesRequest(bool DgiiRejected, bool CertificateExpiring, bool QueueStale, bool CertificationProgress);
public sealed record UnsubscribeRequest(string? Token);

internal static class AlertModel
{
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<AlertPreference>(e =>
        {
            e.ToTable("AlertPreferences");
            e.HasKey(p => new { p.TenantId, p.UserId });
            e.Property(p => p.UserId).HasMaxLength(450);
            e.Property(p => p.UnsubscribeToken).HasMaxLength(64);
            e.HasIndex(p => p.UnsubscribeToken).IsUnique();
        });
        builder.Entity<SentAlert>(e =>
        {
            e.ToTable("SentAlerts");
            e.HasKey(a => new { a.TenantId, a.StoreId, a.Kind, a.Day });
            e.Property(a => a.Kind).HasConversion<string>().HasMaxLength(30);
        });
    }
}

/// <summary>TK-222: e-mails the members about the store's fiscal trouble and certification progress, from what the stores already upload
/// (the heartbeat's fiscal signals, <c>CertificationProgressedV1</c>). It runs right after the inbox applier applied something, never on a timer,
/// so the serverless database still pauses at night (R8 #21). A store that stopped sending gets no fiscal alert; its "no data" issue covers it.</summary>
/// <remarks>shortcut: reads every store's newest heartbeat on each run; scope it to the tenants of the batch when there are thousands of stores.</remarks>
public sealed class AlertNotifier(CloudDbContext db, IEmailSender email, IConfiguration config, TimeProvider clock, Quotas.IQuota quota, ILogger<AlertNotifier> log)
{
    public static readonly TimeSpan CertificateWarning = TimeSpan.FromDays(30);
    public static readonly TimeSpan QueueStaleAfter = TimeSpan.FromHours(24);

    public sealed record Alert(Guid TenantId, Guid StoreId, AlertKind Kind, string Subject, string Text);

    public async Task RunAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var today = BusinessDay.Of(now);
        var alerts = Conditions(
            await db.NodeHeartbeats.AsNoTracking().Where(h => h.SentUtc > now.AddDays(-1)).ToListAsync(ct),
            await db.Certifications.AsNoTracking().Where(c => c.ChangedUtc > now.AddDays(-2)).ToListAsync(ct), now);
        if (alerts.Count == 0) return;

        var disabled = await db.Tenants.Where(t => t.DisabledUtc != null).Select(t => t.Id).ToListAsync(ct);
        var sent = await db.SentAlerts.AsNoTracking().Where(a => a.Day == today).Select(a => new { a.TenantId, a.StoreId, a.Kind }).ToListAsync(ct);
        var stores = await db.Stores.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        foreach (var alert in alerts.Where(a => !disabled.Contains(a.TenantId) && !sent.Contains(new { a.TenantId, a.StoreId, a.Kind })))
        {
            var row = new SentAlert { TenantId = alert.TenantId, StoreId = alert.StoreId, Kind = alert.Kind, Day = today, SentUtc = now };
            db.SentAlerts.Add(row);
            try
            {
                await db.SaveChangesAsync(ct); // taken first: two runs at once cannot both send it
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                continue;
            }
            row.Recipients = await SendAsync(alert, stores.GetValueOrDefault(alert.StoreId, ""), ct);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>What deserves an alert now. Pure: the newest heartbeat of each store and the certification rows.</summary>
    public static List<Alert> Conditions(IReadOnlyList<ReplicaNodeHeartbeat> heartbeats, IReadOnlyList<ReplicaCertification> certifications, DateTime now)
    {
        var alerts = new List<Alert>();
        foreach (var h in heartbeats.GroupBy(h => (h.TenantId, h.StoreId)).Select(g => g.MaxBy(h => h.SentUtc)!))
        {
            if (h.RejectedWithoutReplacement is > 0 and var rejected)
                alerts.Add(new(h.TenantId, h.StoreId, AlertKind.DgiiRejected, "La DGII rechazó comprobantes",
                    $"La DGII rechazó {Count(rejected, "comprobante electrónico", "comprobantes electrónicos")} y todavía no tienen reemplazo. " +
                    "En la tienda, abra Comprobantes electrónicos y revise el motivo."));
            if (h.CertificateExpiresUtc is { } expires && expires - now <= CertificateWarning)
                alerts.Add(new(h.TenantId, h.StoreId, AlertKind.CertificateExpiring, "El certificado digital está por vencer",
                    expires <= now
                        ? "El certificado digital para firmar los comprobantes electrónicos ya venció. Sin él la tienda no puede emitir e-CF: renuévelo con su proveedor."
                        : $"El certificado digital para firmar los comprobantes electrónicos vence el {expires.AddHours(-4):dd/MM/yyyy} " +
                          $"(en {Count((int)Math.Ceiling((expires - now).TotalDays), "día", "días")}). Renuévelo con su proveedor e impórtelo en la tienda."));
            if (h.OldestPendingUtc is { } pending && h.SentUtc - pending > QueueStaleAfter)
                alerts.Add(new(h.TenantId, h.StoreId, AlertKind.QueueStale, "Hay comprobantes sin enviar a la DGII",
                    $"Hay comprobantes electrónicos esperando desde hace {Count((int)(h.SentUtc - pending).TotalHours, "hora", "horas")} para llegar a la DGII " +
                    "(el plazo es de 72 horas). Revise el internet de la tienda."));
        }
        // A step ticked today (store day); a new day without a new tick sends nothing again.
        foreach (var c in certifications.Where(c => BusinessDay.Of(c.ChangedUtc) == BusinessDay.Of(now)))
            alerts.Add(new(c.TenantId, c.StoreId, AlertKind.CertificationProgress, c.Certified ? "Certificación DGII completada" : "Avance en la certificación DGII",
                c.Certified
                    ? "La tienda completó la certificación de facturación electrónica de la DGII."
                    : $"La tienda va por el paso {Math.Min(c.CurrentStep + 1, c.TotalSteps)} de {c.TotalSteps} de la certificación de facturación electrónica " +
                      $"({c.StepsDone} {(c.StepsDone == 1 ? "paso listo" : "pasos listos")})."));
        return alerts;
    }

    private async Task<int> SendAsync(Alert alert, string storeName, CancellationToken ct)
    {
        var members = await db.TenantMembers.AsNoTracking().Where(m => m.TenantId == alert.TenantId)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { m.UserId, m.Role, u.Email }).ToListAsync(ct);
        var prefs = await db.AlertPreferences.Where(p => p.TenantId == alert.TenantId).ToDictionaryAsync(p => p.UserId, ct);
        var panel = MemberEndpoints.PanelUrl(config);
        var count = 0;
        foreach (var m in members.Where(m => m.Email is not null))
        {
            if (!prefs.TryGetValue(m.UserId, out var pref))
            {
                db.AlertPreferences.Add(pref = AlertPreference.For(alert.TenantId, m.UserId, m.Role));
                await db.SaveChangesAsync(ct);
            }
            if (!pref.Wants(alert.Kind)) continue;
            // TK-248: the business's e-mails of the month; a month spent stops alerts (logged) rather than the bill.
            if (!(await quota.TryConsumeAsync(alert.TenantId, Quotas.Meter.Emails, 1, ct)).Allowed)
            {
                log.LogWarning("Alert {Kind} not e-mailed: tenant {TenantId} used its e-mails of the month", alert.Kind, alert.TenantId);
                break;
            }
            try
            {
                await email.SendAsync(m.Email!, $"Abastalia · {storeName}: {alert.Subject}",
                    $"{alert.Text}\n\nTienda: {storeName}\n\nCambie qué avisos recibe en {panel}/app/notifications\n" +
                    $"Para no recibir más avisos: {panel}/unsubscribe#{pref.UnsubscribeToken}", ct);
                count++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // At most once: the alert is taken for today, so a provider outage loses it rather than repeating it later.
                log.LogWarning(e, "Alert {Kind} could not be e-mailed to a member of tenant {TenantId}", alert.Kind, alert.TenantId);
            }
        }
        return count;
    }

    private static string Count(int n, string one, string many) => n.ToString("N0", CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);
}

internal static class AlertEndpoints
{
    public static void MapAlerts(this IEndpointRouteBuilder app)
    {
        // Every member chooses their own alerts (also a read-only one).
        var mine = app.MapGroup("/api/notifications").RequireAuthorization(CloudAuth.PanelPolicy);

        mine.MapGet("", async (ClaimsPrincipal user, CloudDbContext db, IEmailSender email, CancellationToken ct) =>
        {
            var pref = await MineAsync(user, db, ct);
            return Results.Ok(ToResponse(pref, user, email));
        });

        mine.MapPut("", async (AlertPreferencesRequest r, ClaimsPrincipal user, CloudDbContext db, IEmailSender email, CancellationToken ct) =>
        {
            var pref = await MineAsync(user, db, ct);
            (pref.DgiiRejected, pref.CertificateExpiring, pref.QueueStale, pref.CertificationProgress, pref.Unsubscribed) =
                (r.DgiiRejected, r.CertificateExpiring, r.QueueStale, r.CertificationProgress, false);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResponse(pref, user, email));
        });

        // The link in every alert e-mail: no session. R8 #12: the token is after "#" in the link and travels in the body.
        app.MapPost("/api/notifications/unsubscribe", async (UnsubscribeRequest r, CloudDbContext db, CancellationToken ct) =>
            r.Token is { Length: 64 } token && await db.AlertPreferences.Where(p => p.UnsubscribeToken == token)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Unsubscribed, true), ct) == 1
                ? Results.NoContent()
                : EndpointExtensions.Problem(Error.NotFound("Alerts.InvalidLink", "El enlace no es válido. Cambie sus avisos desde el panel.")))
            .AllowAnonymous();
    }

    private static async Task<AlertPreference> MineAsync(ClaimsPrincipal user, CloudDbContext db, CancellationToken ct)
    {
        var (tenantId, userId) = (user.GetTenantId(), user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var pref = await db.AlertPreferences.SingleOrDefaultAsync(p => p.TenantId == tenantId && p.UserId == userId, ct);
        if (pref is not null) return pref;
        var role = await db.TenantMembers.Where(m => m.TenantId == tenantId && m.UserId == userId).Select(m => m.Role).SingleAsync(ct);
        db.AlertPreferences.Add(pref = AlertPreference.For(tenantId, userId, role));
        await db.SaveChangesAsync(ct);
        return pref;
    }

    private static AlertPreferences ToResponse(AlertPreference p, ClaimsPrincipal user, IEmailSender email) => new(user.FindFirstValue(ClaimTypes.Email) ?? user.Identity?.Name,
        p.DgiiRejected, p.CertificateExpiring, p.QueueStale, p.CertificationProgress, p.Unsubscribed, email.Delivers);
}

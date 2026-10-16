using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Identity.Contracts;
using Toki.SharedKernel;

namespace Toki.Reports;

/// <summary>One audit record as the back office reads it. <see cref="Data"/> is the JSON the action saved (what was asked or changed).
/// ResourceId is null for store-wide records (the business settings).</summary>
internal sealed record AuditRow(long Sequence, DateTime DateUtc, Guid? UserId, string? User, string? Supervisor, string Action, string ResourceType,
    Guid? ResourceId, string Data);

/// <summary>TK-213: the audit log for whoever has audit.view, newest first: pages of <see cref="PageSize"/> as JSON, or the whole period as CSV/Excel.</summary>
internal sealed class AuditEndpoints : IEndpoint
{
    public const int PageSize = 100;

    public void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/audit").RequirePermission(Permissions.AuditView).WithTags("Audit");

        g.MapGet("", async ([AsParameters] Period p, Guid? userId, string? action, int? page, InfraDbContext db, IUserDirectory users, IUserContext user,
            CancellationToken ct) =>
        {
            if (p.Invalid() is { } invalid) return invalid;
            var (from, to) = BusinessDay.UtcRange(p.From, p.To);
            var q = db.AuditRecords.AsNoTracking().Where(r => r.DateUtc >= from && r.DateUtc < to);
            if (userId is { } u) q = q.Where(r => r.UserId == u || r.SupervisorId == u);
            if (!string.IsNullOrWhiteSpace(action)) q = q.Where(r => r.Action == action);
            q = q.OrderByDescending(r => r.Sequence);
            var file = p.Format?.ToLowerInvariant() is "csv" or "xlsx";
            var records = await (file ? q : q.Skip((Math.Max(page ?? 1, 1) - 1) * PageSize).Take(PageSize)).ToListAsync(ct);

            var costs = user.Has(Permissions.CostsView);
            var names = (await users.AllAsync(ct)).ToDictionary(n => n.Id, n => n.Name);
            string? Name(Guid? id) => id is { } x ? names.GetValueOrDefault(x) : null;
            var rows = records.Select(r => new AuditRow(r.Sequence, r.DateUtc, r.UserId, Name(r.UserId), Name(r.SupervisorId), r.Action, r.ResourceType,
                r.ResourceId == Guid.Empty ? null : r.ResourceId, costs ? r.Data : WithoutCosts(r.Data))).ToList();
            return Export.As(rows, p.Format, p.Name("auditoria"));
        });

        g.MapGet("/users", async (IUserDirectory users, CancellationToken ct) => Results.Ok(await users.AllAsync(ct)));
    }

    /// <summary>R11 A12: product, purchase and stock records carry costs; without costs.view every field whose name has "cost" comes back null.</summary>
    public static string WithoutCosts(string data)
    {
        if (!data.Contains("ost", StringComparison.OrdinalIgnoreCase)) return data;
        try
        {
            var node = JsonNode.Parse(data);
            Redact(node);
            return node?.ToJsonString() ?? data;
        }
        catch (JsonException) { return data; }

        static void Redact(JsonNode? n)
        {
            if (n is JsonObject o)
                foreach (var (key, value) in o.ToList())
                    if (key.Contains("cost", StringComparison.OrdinalIgnoreCase)) o[key] = null;
                    else Redact(value);
            else if (n is JsonArray a) foreach (var item in a) Redact(item);
        }
    }
}

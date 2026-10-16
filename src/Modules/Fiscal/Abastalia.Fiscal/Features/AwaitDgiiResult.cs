using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.Fiscal.Contracts;
using Abastalia.Fiscal.Domain;

namespace Abastalia.Fiscal.Features;

/// <summary>TK-208 (owner, D6: yes, wait): before printing a 31 the register waits up to <c>Fiscal:WaitForDgiiSeconds</c> (5; 0 turns it off) for the
/// DGII's answer, so the customer gets the invoice already accepted. The send starts at once in its own scope and is never cut: if the deadline
/// passes first, the receipt says "pendiente" and the queue follows the e-CF as always. Any signed-in register may ask.</summary>
internal static class AwaitDgiiResult
{
    /// <param name="Status">Accepted, AcceptedConditional, Rejected, Simulated or Pending (no answer yet: the queue keeps it).</param>
    public sealed record Response(string Status, string? Message);

    public const int MaxSeconds = 15;

    public static string StatusOf(SubmissionStatus s) => s switch
    {
        SubmissionStatus.Accepted => "Accepted",
        SubmissionStatus.AcceptedConditional => "AcceptedConditional",
        SubmissionStatus.Rejected => "Rejected",
        SubmissionStatus.Simulated => "Simulated",
        _ => "Pending",
    };

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/fiscal/documents/{eNcf}/await-result", async (string eNcf, FiscalDbContext db, FiscalMode mode, IServiceScopeFactory scopes,
                IHostApplicationLifetime lifetime, IConfiguration config, TimeProvider clock, ILogger<EcfSubmissionQueue> log, CancellationToken ct) =>
                {
                    if (eNcf.Length > 13) return Results.NotFound();
                    var doc = await db.Documents.AsNoTracking().Where(d => d.ENcf == eNcf).OrderByDescending(d => d.SignedUtc)
                        .Select(d => new { d.Id, d.Type, d.Status }).FirstOrDefaultAsync(ct);
                    if (doc is null) return Results.NotFound();

                    // Only a 31 waits, and only when this store sends to the DGII on its own (not in Simulated).
                    var wait = doc.Type == EcfType.FiscalCredit31 && mode.AutoSubmit
                        ? TimeSpan.FromSeconds(Math.Clamp(config.GetValue("Fiscal:WaitForDgiiSeconds", 5), 0, MaxSeconds))
                        : TimeSpan.Zero;
                    if (doc.Status == SubmissionStatus.Pending && wait > TimeSpan.Zero)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await using var scope = scopes.CreateAsyncScope();
                                await scope.ServiceProvider.GetRequiredService<EcfSubmissionQueue>().SendNowAsync(doc.Id, wait, lifetime.ApplicationStopping);
                            }
                            catch (Exception ex) when (!lifetime.ApplicationStopping.IsCancellationRequested)
                            {
                                log.LogWarning(ex, "Could not send {ENcf} right away; the queue will", eNcf); // e.g. the worker answered first
                            }
                        }, CancellationToken.None);
                    }

                    var until = clock.GetUtcNow() + wait;
                    while (true)
                    {
                        var now = await db.Documents.AsNoTracking().Where(d => d.Id == doc.Id).Select(d => new { d.Status, d.DgiiMessages }).SingleAsync(ct);
                        if (now.Status is not (SubmissionStatus.Pending or SubmissionStatus.Sent) || clock.GetUtcNow() >= until)
                            return Results.Ok(new Response(StatusOf(now.Status), now.Status == SubmissionStatus.Rejected ? now.DgiiMessages : null));
                        await Task.Delay(TimeSpan.FromMilliseconds(250), clock, ct);
                    }
                })
                .RequireAuthorization().WithTags("Fiscal");
    }
}

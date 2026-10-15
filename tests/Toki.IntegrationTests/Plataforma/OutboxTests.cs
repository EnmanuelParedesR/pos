using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.Caja.Contracts;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Plataforma;

/// <summary>R6 #8: messages that keep failing cannot stall the outbox. Own app: it writes broken messages.</summary>
public sealed class OutboxTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task Mensajes_que_fallan_no_detienen_a_los_que_vienen_despues()
    {
        await app.AdminAsync();
        var tipo = typeof(TurnoCerradoV1).FullName!;
        var ahora = app.Reloj.GetUtcNow().UtcDateTime;
        var bueno = new TurnoCerradoV1(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 0, 0, 0);
        await app.EnScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<InfraDbContext>();
            // 150 messages whose handler fails (a payload it cannot read), then one that works.
            db.Outbox.AddRange(Enumerable.Range(0, 150).Select(_ => new OutboxMensaje
                { Id = Guid.NewGuid(), Tipo = tipo, AgregadoId = Guid.NewGuid(), Payload = "{roto", OcurridoUtc = ahora }));
            db.Outbox.Add(new OutboxMensaje
                { Id = bueno.EventId, Tipo = tipo, AgregadoId = bueno.TurnoId, Payload = System.Text.Json.JsonSerializer.Serialize(bueno, Json.Opciones), OcurridoUtc = ahora });
            return await db.SaveChangesAsync();
        });

        // One run ends (it does not spin on the failures) and delivers the good message.
        var tomados = await app.EnScopeAsync(sp => sp.GetRequiredService<ProcesadorOutbox>().ProcesarAsync(CancellationToken.None));
        tomados.ShouldBeGreaterThanOrEqualTo(151);
        (await app.EnScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().Outbox.SingleAsync(m => m.Id == bueno.EventId))).ProcesadoUtc.ShouldNotBeNull();
        (await app.EnScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().Outbox.CountAsync(m => m.Payload == "{roto" && m.ProcesadoUtc == null))).ShouldBe(150);
    }
}

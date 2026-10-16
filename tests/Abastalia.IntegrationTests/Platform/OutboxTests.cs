using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.CashRegister.Contracts;
using Abastalia.IntegrationTests.Infra;

namespace Abastalia.IntegrationTests.Platform;

/// <summary>R6 #8: messages that keep failing cannot stall the outbox. Own app: it writes broken messages.</summary>
public sealed class OutboxTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    [Fact]
    public async Task Failing_messages_do_not_hold_up_the_ones_that_come_after()
    {
        await app.AdminAsync();
        var type = typeof(ShiftClosedV1).FullName!;
        var now = app.Clock.GetUtcNow().UtcDateTime;
        var good = new ShiftClosedV1(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 0, 0, 0);
        await app.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<InfraDbContext>();
            // 150 messages whose handler fails (a payload it cannot read), then one that works.
            db.Outbox.AddRange(Enumerable.Range(0, 150).Select(_ => new OutboxMessage
                { Id = Guid.NewGuid(), Type = type, AggregateId = Guid.NewGuid(), Payload = "{roto", OccurredUtc = now }));
            db.Outbox.Add(new OutboxMessage
                { Id = good.EventId, Type = type, AggregateId = good.ShiftId, Payload = System.Text.Json.JsonSerializer.Serialize(good, Json.Options), OccurredUtc = now });
            return await db.SaveChangesAsync();
        });

        // One run ends (it does not spin on the failures) and delivers the good message.
        var taken = await app.InScopeAsync(sp => sp.GetRequiredService<OutboxProcessor>().ProcessAsync(CancellationToken.None));
        taken.ShouldBeGreaterThanOrEqualTo(151);
        (await app.InScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().Outbox.SingleAsync(m => m.Id == good.EventId))).ProcessedUtc.ShouldNotBeNull();
        (await app.InScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().Outbox.CountAsync(m => m.Payload == "{roto" && m.ProcessedUtc == null))).ShouldBe(150);
    }
}

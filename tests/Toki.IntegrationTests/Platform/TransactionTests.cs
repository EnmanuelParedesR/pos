using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.Settings;
using Toki.IntegrationTests.Infra;
using Toki.Inventory.Persistence;
using Toki.SharedKernel;

namespace Toki.IntegrationTests.Platform;

/// <summary>TK-003/TK-004: a command writes every module, the outbox and the audit trail, or nothing at all.</summary>
public sealed class TransactionTests(TokiApp app) : IClassFixture<TokiApp>
{
    private sealed record State(bool Category, bool Outbox, long AuditHead, string Business);

    private Task<State> StateAsync(Guid marker) => app.InScopeAsync(async sp =>
    {
        var infra = sp.GetRequiredService<InfraDbContext>();
        return new State(
            await sp.GetRequiredService<InventoryDbContext>().Categories.AnyAsync(c => c.Id == marker),
            await infra.Outbox.AnyAsync(m => m.Id == marker),
            (await infra.AuditHead.SingleAsync()).Sequence,
            (await sp.GetRequiredService<SettingsDbContext>().Business.SingleAsync()).Name);
    });

    private async Task<Result<Ok>> RunAsync(WriteAndFail c)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICommandHandler<WriteAndFail, Ok>>().Handle(c, CancellationToken.None);
    }

    [Fact]
    public async Task An_error_rolls_everything_back()
    {
        var marker = Guid.CreateVersion7();
        var before = await StateAsync(marker);

        var r = await RunAsync(new WriteAndFail(marker, Failure.Error));

        r.Error!.Code.ShouldBe("Test.Failure");
        (await StateAsync(marker)).ShouldBe(before);
    }

    [Fact]
    public async Task An_exception_rolls_everything_back()
    {
        var marker = Guid.CreateVersion7();
        var before = await StateAsync(marker);

        await Should.ThrowAsync<InvalidOperationException>(() => RunAsync(new WriteAndFail(marker, Failure.Exception)));

        (await StateAsync(marker)).ShouldBe(before);
    }

    [Fact]
    public async Task Without_a_failure_everything_is_saved_together()
    {
        var marker = Guid.CreateVersion7();
        var before = await StateAsync(marker);

        (await RunAsync(new WriteAndFail(marker, Failure.None))).IsSuccess.ShouldBeTrue();

        var after = await StateAsync(marker);
        after.Category.ShouldBeTrue();
        after.Outbox.ShouldBeTrue();
        after.AuditHead.ShouldBe(before.AuditHead + 1);
        after.Business.ShouldBe("NO DEBE QUEDAR");
        (await app.VerifyAuditAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task Auditing_outside_a_command_is_rejected() =>
        await Should.ThrowAsync<InvalidOperationException>(() => app.InScopeAsync(async sp =>
        {
            await sp.GetRequiredService<IAuditLog>().RecordAsync("x", "x", Guid.Empty, new { }, CancellationToken.None);
            return 0;
        }));
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Settings;
using Toki.Inventory.Contracts;
using Toki.Inventory.Domain;
using Toki.Inventory.Persistence;
using Toki.SharedKernel;

namespace Toki.IntegrationTests.Infra;

public enum Failure { None, Error, Exception }

/// <summary>Writes to two modules, the outbox and the audit trail, then fails (or not). Proves the transaction is all-or-nothing.</summary>
internal sealed record WriteAndFail(Guid Marker, Failure Failure) : ICommand<Ok>;

/// <summary>A command that needs a supervisor, to exercise the approval decorator.</summary>
internal sealed record SupervisedAction(Guid ResourceId, Guid? SupervisorApprovalId) : ICommand<Ok>, IRequiresApproval
{
    public string Permission => Permissions.SalesVoid;
}

internal sealed class WriteAndFailHandler(InventoryDbContext inventory, SettingsDbContext settings, IOutbox outbox, IAuditLog audit)
    : ICommandHandler<WriteAndFail, Ok>
{
    public async Task<Result<Ok>> Handle(WriteAndFail c, CancellationToken ct)
    {
        inventory.Categories.Add(new Category { Id = c.Marker, Name = "marca-" + c.Marker });
        await inventory.SaveChangesAsync(ct); // an early save inside the transaction must roll back too
        await settings.Business.ExecuteUpdateAsync(s => s.SetProperty(n => n.Name, "NO DEBE QUEDAR"), ct);
        outbox.Add(new PriceChangedV1(c.Marker, 1, 2, null, null) { EventId = c.Marker });
        await audit.RecordAsync("test", "Test", c.Marker, new { }, ct);
        return c.Failure switch
        {
            Failure.Error => Error.Validation("Test.Failure", "falla a propósito"),
            Failure.Exception => throw new InvalidOperationException("falla a propósito"),
            _ => Ok.Value,
        };
    }
}

/// <summary>Moves stock through the public contract the way a sale (out) or a purchase (in, with cost) will.</summary>
internal sealed record MoveStock(Guid ProductId, decimal Quantity, bool Inbound, decimal? Cost = null) : ICommand<Ok>;

internal sealed class MoveStockHandler(IStockMovements movements) : ICommandHandler<MoveStock, Ok>
{
    public async Task<Result<Ok>> Handle(MoveStock c, CancellationToken ct)
    {
        var line = new[] { new StockMovementLine(c.ProductId, c.Quantity, c.Cost) };
        var r = c.Inbound
            ? await movements.RecordInboundAsync(StockMovementType.Purchase, Guid.CreateVersion7(), line, updateCost: true, ct)
            : await movements.RecordOutboundAsync(StockMovementType.Sale, Guid.CreateVersion7(), line, ct);
        return r.IsFailure ? r.Error! : Ok.Value;
    }
}

internal sealed class SupervisedActionHandler : ICommandHandler<SupervisedAction, Ok>
{
    public Task<Result<Ok>> Handle(SupervisedAction c, CancellationToken ct) => Task.FromResult<Result<Ok>>(Ok.Value);
}

internal static class TestCommands
{
    public static void Register(IServiceCollection s)
    {
        Decorated<WriteAndFail, WriteAndFailHandler>(s);
        Decorated<SupervisedAction, SupervisedActionHandler>(s);
        Decorated<MoveStock, MoveStockHandler>(s);
    }

    // Same pipeline as production commands: Validation → Transaction → Approval → handler.
    private static void Decorated<TCommand, THandler>(IServiceCollection s)
        where TCommand : ICommand<Ok> where THandler : class, ICommandHandler<TCommand, Ok>
    {
        s.AddScoped<ICommandHandler<TCommand, Ok>, THandler>();
        s.Decorate<ICommandHandler<TCommand, Ok>, ApprovalDecorator<TCommand, Ok>>();
        s.Decorate<ICommandHandler<TCommand, Ok>, TransactionDecorator<TCommand, Ok>>();
        s.Decorate<ICommandHandler<TCommand, Ok>, ValidationDecorator<TCommand, Ok>>();
    }
}

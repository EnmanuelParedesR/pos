using FluentValidation;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.SharedKernel;

namespace Toki.BuildingBlocks.Messaging;

// Order, outside in: Validation → Transaction → Approval → handler.
// Approval runs inside the transaction so a supervisor approval is consumed atomically with the command.

internal sealed class ValidationDecorator<TCommand, TResponse>(
    ICommandHandler<TCommand, TResponse> inner, IEnumerable<IValidator<TCommand>> validators)
    : ICommandHandler<TCommand, TResponse> where TCommand : ICommand<TResponse>
{
    public async Task<Result<TResponse>> Handle(TCommand command, CancellationToken ct)
    {
        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var v in validators) failures.AddRange((await v.ValidateAsync(command, ct)).Errors);
        if (failures.Count == 0) return await inner.Handle(command, ct);
        return Error.Validation("Validation", failures[0].ErrorMessage) with
        {
            Details = failures.GroupBy(f => f.PropertyName).ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).ToArray()),
        };
    }
}

internal sealed class TransactionDecorator<TCommand, TResponse>(
    ICommandHandler<TCommand, TResponse> inner, UnitOfWork uow, IEnumerable<ModuleDbContext> contexts)
    : ICommandHandler<TCommand, TResponse> where TCommand : ICommand<TResponse>
{
    public async Task<Result<TResponse>> Handle(TCommand command, CancellationToken ct)
    {
        if (uow.Transaction is not null)
            throw new InvalidOperationException("Commands cannot be nested; call the other module's Contracts interface instead.");

        var dbs = contexts.ToList();
        var tx = await uow.BeginAsync(ct);
        var commit = false;
        try
        {
            foreach (var db in dbs) await db.Database.UseTransactionAsync(tx, ct);
            var result = await inner.Handle(command, ct);
            if (result.IsFailure) return result;

            foreach (var db in dbs.Where(d => d.ChangeTracker.HasChanges())) await db.SaveChangesAsync(ct);
            commit = true;
            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("Concurrency", "Otro usuario cambió este registro. Recargue e intente de nuevo.");
        }
        catch (DbUpdateException e) when (IsDuplicate(e))
        {
            return Error.Conflict("Duplicate", "Ya existe un registro con esos datos.");
        }
        catch (SqlException e) when (e.Number == 1205)
        {
            // Deadlock victim: nothing was saved, the register can simply retry.
            return Error.Conflict("Busy", "El sistema estaba ocupado. Intente de nuevo.");
        }
        finally
        {
            try { await uow.EndAsync(commit); }
            finally
            {
                foreach (var db in dbs)
                {
                    await db.Database.UseTransactionAsync(null, CancellationToken.None);
                    if (!commit) db.ChangeTracker.Clear();
                }
            }
        }
    }

    private static bool IsDuplicate(DbUpdateException e) => e.InnerException switch
    {
        SqlException s => s.Number is 2601 or 2627,
        SqliteException s => s.SqliteExtendedErrorCode is 2067 or 1555,
        _ => false,
    };
}

internal sealed class ApprovalDecorator<TCommand, TResponse>(
    ICommandHandler<TCommand, TResponse> inner, IUserContext user, ISupervisorApprovals approvals, IAuditLog audit)
    : ICommandHandler<TCommand, TResponse> where TCommand : ICommand<TResponse>
{
    public async Task<Result<TResponse>> Handle(TCommand command, CancellationToken ct)
    {
        if (command is IRequiresApproval req && !user.Has(req.Permission))
        {
            if (req.SupervisorApprovalId is not { } id) return ApprovalErrors.Required(req.Permission);
            var supervisor = await approvals.ConsumeAsync(id, req.Permission, req.ResourceId, ct);
            if (supervisor.IsFailure) return supervisor.Error!;
            user.SupervisorId = supervisor.Value;
            var result = await inner.Handle(command, ct);
            // Written after the handler: the audit head must be the last lock a command takes (no deadlock with stock rows).
            if (result.IsSuccess) await audit.RecordAsync("approval.used", typeof(TCommand).Name, req.ResourceId, new { req.Permission, Approval = id }, ct);
            return result;
        }
        return await inner.Handle(command, ct);
    }
}

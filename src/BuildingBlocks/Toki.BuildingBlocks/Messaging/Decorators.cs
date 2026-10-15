using FluentValidation;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.SharedKernel;

namespace Toki.BuildingBlocks.Messaging;

// Order, outside in: Validacion → Transaccion → Autorizacion → handler.
// Authorization runs inside the transaction so a supervisor authorization is consumed atomically with the command.

internal sealed class ValidacionDecorator<TCommand, TResponse>(
    ICommandHandler<TCommand, TResponse> inner, IEnumerable<IValidator<TCommand>> validators)
    : ICommandHandler<TCommand, TResponse> where TCommand : ICommand<TResponse>
{
    public async Task<Result<TResponse>> Handle(TCommand command, CancellationToken ct)
    {
        var fallos = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var v in validators) fallos.AddRange((await v.ValidateAsync(command, ct)).Errors);
        if (fallos.Count == 0) return await inner.Handle(command, ct);
        return Error.Validation("Validacion", fallos[0].ErrorMessage) with
        {
            Details = fallos.GroupBy(f => f.PropertyName).ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).ToArray()),
        };
    }
}

internal sealed class TransaccionDecorator<TCommand, TResponse>(
    ICommandHandler<TCommand, TResponse> inner, UnitOfWork uow, IEnumerable<ModuleDbContext> contextos)
    : ICommandHandler<TCommand, TResponse> where TCommand : ICommand<TResponse>
{
    public async Task<Result<TResponse>> Handle(TCommand command, CancellationToken ct)
    {
        if (uow.Transaction is not null)
            throw new InvalidOperationException("Commands cannot be nested; call the other module's Contracts interface instead.");

        var dbs = contextos.ToList();
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
            return Error.Conflict("Concurrencia", "Otro usuario cambió este registro. Recargue e intente de nuevo.");
        }
        catch (DbUpdateException e) when (EsDuplicado(e))
        {
            return Error.Conflict("Duplicado", "Ya existe un registro con esos datos.");
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

    private static bool EsDuplicado(DbUpdateException e) => e.InnerException switch
    {
        SqlException s => s.Number is 2601 or 2627,
        SqliteException s => s.SqliteExtendedErrorCode is 2067 or 1555,
        _ => false,
    };
}

internal sealed class AutorizacionDecorator<TCommand, TResponse>(
    ICommandHandler<TCommand, TResponse> inner, IUserContext usuario, IAutorizacionesSupervisor autorizaciones, IAuditoria auditoria)
    : ICommandHandler<TCommand, TResponse> where TCommand : ICommand<TResponse>
{
    public async Task<Result<TResponse>> Handle(TCommand command, CancellationToken ct)
    {
        if (command is IRequiereAutorizacion req && !usuario.Tiene(req.Permiso))
        {
            if (req.AutorizacionSupervisorId is not { } id) return AutorizacionErrors.Requerida(req.Permiso);
            var supervisor = await autorizaciones.ConsumirAsync(id, req.Permiso, req.RecursoId, ct);
            if (supervisor.IsFailure) return supervisor.Error!;
            usuario.SupervisorId = supervisor.Value;
            await auditoria.RegistrarAsync("autorizacion.usada", typeof(TCommand).Name, req.RecursoId, new { req.Permiso, Autorizacion = id }, ct);
        }
        return await inner.Handle(command, ct);
    }
}

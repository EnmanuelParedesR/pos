using FluentValidation;
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
    public async Task<Result<TResponse>> Handle(TCommand command, CancellationToken ct) =>
        await Validacion.ValidarAsync(command, validators, ct) is { } error ? error : await inner.Handle(command, ct);
}

internal sealed class ValidacionQueryDecorator<TQuery, TResponse>(
    IQueryHandler<TQuery, TResponse> inner, IEnumerable<IValidator<TQuery>> validators)
    : IQueryHandler<TQuery, TResponse> where TQuery : IQuery<TResponse>
{
    public async Task<Result<TResponse>> Handle(TQuery query, CancellationToken ct) =>
        await Validacion.ValidarAsync(query, validators, ct) is { } error ? error : await inner.Handle(query, ct);
}

internal static class Validacion
{
    public static async Task<Error?> ValidarAsync<T>(T request, IEnumerable<IValidator<T>> validators, CancellationToken ct)
    {
        var fallos = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var v in validators) fallos.AddRange((await v.ValidateAsync(request, ct)).Errors);
        if (fallos.Count == 0) return null;
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
        if (uow.Transaction is not null) return await inner.Handle(command, ct); // nested command: join the outer transaction

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
        finally
        {
            await uow.EndAsync(commit, ct);
            foreach (var db in dbs)
            {
                await db.Database.UseTransactionAsync(null, CancellationToken.None);
                if (!commit) db.ChangeTracker.Clear();
            }
        }
    }
}

internal sealed class AutorizacionDecorator<TCommand, TResponse>(
    ICommandHandler<TCommand, TResponse> inner, IUserContext usuario, IAutorizacionesSupervisor autorizaciones)
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
        }
        return await inner.Handle(command, ct);
    }
}

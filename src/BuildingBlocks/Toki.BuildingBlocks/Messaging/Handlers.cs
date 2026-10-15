using Toki.SharedKernel;

namespace Toki.BuildingBlocks.Messaging;

/// <summary>A change. Runs inside one database transaction (see <see cref="TransaccionDecorator{TCommand,TResponse}"/>).</summary>
public interface ICommand<TResponse>;

public interface ICommandHandler<in TCommand, TResponse> where TCommand : ICommand<TResponse>
{
    Task<Result<TResponse>> Handle(TCommand command, CancellationToken ct);
}

/// <summary>A read. No transaction, no side effects.</summary>
public interface IQuery<TResponse>;

public interface IQueryHandler<in TQuery, TResponse> where TQuery : IQuery<TResponse>
{
    Task<Result<TResponse>> Handle(TQuery query, CancellationToken ct);
}

/// <summary>Response for commands that return nothing but success.</summary>
public sealed record Ok
{
    public static readonly Ok Valor = new();
}

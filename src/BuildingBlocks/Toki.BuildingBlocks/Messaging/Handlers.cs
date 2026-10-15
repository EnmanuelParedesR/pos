using Toki.SharedKernel;

namespace Toki.BuildingBlocks.Messaging;

/// <summary>A change. Runs inside one database transaction (see TransaccionDecorator). Reads live in the endpoints.</summary>
public interface ICommand<TResponse>;

public interface ICommandHandler<in TCommand, TResponse> where TCommand : ICommand<TResponse>
{
    Task<Result<TResponse>> Handle(TCommand command, CancellationToken ct);
}

/// <summary>Response for commands that return nothing but success.</summary>
public sealed record Ok
{
    public static readonly Ok Valor = new();
}

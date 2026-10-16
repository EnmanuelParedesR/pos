namespace Abastalia.SharedKernel;

public enum ErrorType { Failure, Validation, NotFound, Conflict, Unauthorized, Forbidden }

/// <summary>An expected failure with a stable code (for example "CashRegister.ShiftAlreadyOpen").</summary>
public sealed record Error(string Code, string Description, ErrorType Type = ErrorType.Failure)
{
    public IReadOnlyDictionary<string, string[]>? Details { get; init; }

    public static Error Validation(string code, string description) => new(code, description, ErrorType.Validation);
    public static Error NotFound(string code, string description) => new(code, description, ErrorType.NotFound);
    public static Error Conflict(string code, string description) => new(code, description, ErrorType.Conflict);
    public static Error Unauthorized(string code, string description) => new(code, description, ErrorType.Unauthorized);
    public static Error Forbidden(string code, string description) => new(code, description, ErrorType.Forbidden);
}

public class Result
{
    protected Result(Error? error) => Error = error;

    public Error? Error { get; }
    public bool IsSuccess => Error is null;
    public bool IsFailure => !IsSuccess;

    public static Result Success() => new(null);
    public static Result Failure(Error error) => new(error);
    public static Result<T> Success<T>(T value) => new(value, null);

    public static implicit operator Result(Error error) => Failure(error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    internal Result(T? value, Error? error) : base(error) => _value = value;

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException($"Result failed: {Error!.Code}");

    public static implicit operator Result<T>(T value) => new(value, null);
    public static implicit operator Result<T>(Error error) => new(default, error);
}

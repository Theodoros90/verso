namespace Verso.Application;

public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
    PreconditionRequired,
    PreconditionFailed,
    Forbidden,
}

/// <summary>An expected failure of a use case. <see cref="Code"/> is stable and documented; clients can rely on it.</summary>
public sealed record AppError(ErrorKind Kind, string Code, string Message, IReadOnlyDictionary<string, string[]>? Fields = null, IReadOnlyDictionary<string, object?>? Extensions = null)
{
    public static AppError Validation(IReadOnlyDictionary<string, string[]> fields) =>
        new(ErrorKind.Validation, "validation", "One or more fields are invalid.", fields);

    public static AppError NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);
    public static AppError Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);
}

/// <summary>Either a value or an <see cref="AppError"/>.</summary>
public sealed class Result<T>
{
    internal Result(T? value, AppError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }
    public AppError? Error { get; }
    public bool IsSuccess => Error is null;

    public static implicit operator Result<T>(T value) => new(value, null);
    public static implicit operator Result<T>(AppError error) => new(default, error);
}

public static class Result
{
    /// <summary>For values whose static type is an interface (implicit conversions from interfaces aren't allowed).</summary>
    public static Result<T> Success<T>(T value) => new(value, null);
}

using Verso.Application;

namespace Verso.Api.Common;

/// <summary>Maps application errors to RFC 9457 problem responses with a stable <c>code</c> extension.</summary>
public static class ErrorResults
{
    public static IResult ToProblem(this AppError error)
    {
        if (error.Kind == ErrorKind.Validation && error.Fields is not null)
        {
            return Results.ValidationProblem(
                error.Fields.ToDictionary(f => f.Key, f => f.Value),
                detail: error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = error.Code });
        }

        var status = error.Kind switch
        {
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            ErrorKind.PreconditionRequired => StatusCodes.Status428PreconditionRequired,
            ErrorKind.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
            _ => StatusCodes.Status400BadRequest,
        };
        return Results.Problem(detail: error.Message, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = error.Code });
    }

    /// <summary>200 with the value and its ETag header, or the error as a problem response.</summary>
    public static IResult ToVersionedResult<T>(this Result<Versioned<T>> result, HttpContext http, Func<T, IResult>? onSuccess = null)
    {
        if (!result.IsSuccess)
        {
            return result.Error!.ToProblem();
        }

        http.Response.Headers.ETag = result.Value!.ETag;
        return onSuccess?.Invoke(result.Value.Value) ?? Results.Ok(result.Value.Value);
    }

    public static IResult ToResult<T>(this Result<T> result) =>
        result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem();
}

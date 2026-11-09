namespace Verso.Application;

/// <summary>A value plus the ETag clients must send back in <c>If-Match</c> to change it.</summary>
public sealed record Versioned<T>(T Value, string ETag);

/// <summary>Strong ETags built from SQL Server rowversion values: <c>"base64"</c>.</summary>
public static class ETag
{
    public static string From(byte[] rowVersion) => $"\"{Convert.ToBase64String(rowVersion)}\"";

    /// <summary>
    /// Checks an <c>If-Match</c> header against the current rowversion.
    /// Missing or <c>*</c> → 428 (an explicit ETag is required); anything that doesn't match → 412.
    /// </summary>
    public static AppError? Check(string? ifMatch, byte[] current, out byte[] expected)
    {
        expected = [];
        var value = ifMatch?.Trim();
        if (string.IsNullOrEmpty(value) || value == "*")
        {
            return new AppError(ErrorKind.PreconditionRequired, "precondition.required",
                "Send the ETag you last read in an If-Match header.");
        }

        if (value.StartsWith("W/", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return StaleError();
        }

        try
        {
            expected = Convert.FromBase64String(value[1..^1]);
        }
        catch (FormatException)
        {
            return StaleError();
        }

        return expected.AsSpan().SequenceEqual(current) ? null : StaleError();
    }

    public static AppError StaleError() => new(ErrorKind.PreconditionFailed, "precondition.failed",
        "The draft was changed since you read it. Reload it and apply your changes again.");
}

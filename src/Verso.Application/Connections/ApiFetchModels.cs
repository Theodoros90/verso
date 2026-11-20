using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Verso.Application.Connections;

/// <summary>
/// One GET of the Fetch from API step. <see cref="Path"/> is already resolved and URL-encoded piece by piece (it starts with a single "/" and has no query
/// or fragment); <see cref="Query"/> pairs are raw (the sender encodes names and values). The host is never part of the request: the connection fixes it.
/// </summary>
public sealed record ApiFetchRequest(string ConnectionKey, string Path, IReadOnlyList<KeyValuePair<string, string>> Query, int MaxResponseBytes, TimeSpan Timeout)
{
    /// <summary>The path and query carry run-data values: never printed (a record's ToString would otherwise list them).</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"ConnectionKey = {ConnectionKey}, Path = [redacted], Query = [{Query?.Count ?? 0} value(s) redacted], MaxResponseBytes = {MaxResponseBytes}, Timeout = {Timeout}");
        return true;
    }
}

/// <summary>
/// The outcome of a fetch. <see cref="Body"/> is the parsed JSON answer (it owns its memory) and is the only place the answer appears: callers merge it into
/// the run data and never log it. <see cref="Message"/> is Verso's own wording for <see cref="Code"/>, never the API's text, the address or a secret.
/// A 404 or an empty answer is a success without a body (<see cref="Found"/> false).
/// </summary>
public sealed record ApiFetchResult(string? Code, string? Message, bool Retryable, int? HttpStatus, JsonElement? Body, long ResponseBytes, long DurationMs)
{
    public bool Ok => Code is null;

    public bool Found => Ok && Body is not null;

    public static ApiFetchResult Success(int httpStatus, JsonElement? body, long bytes, long durationMs) => new(null, null, false, httpStatus, body, bytes, durationMs);

    public static ApiFetchResult Failure(string code, bool retryable, int? httpStatus = null, long durationMs = 0) =>
        new(code, ApiFetchMessages.For(code), retryable, httpStatus, null, 0, durationMs);

    /// <summary>A GET is safe to repeat: only timeouts, connection failures, HTTP 5xx and 429 are worth another attempt.</summary>
    public static bool IsRetryable(string code) => code is "api.timeout" or "api.unreachable" or "api.server_error" or "api.rate_limited";

    /// <summary>The answer is third-party data: ToString shows only its kind.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"Code = {Code}, Retryable = {Retryable}, HttpStatus = {HttpStatus}, Body = {(Body is { } b ? $"[{b.ValueKind} redacted]" : "null")}, ResponseBytes = {ResponseBytes}, DurationMs = {DurationMs}");
        return true;
    }
}

/// <summary>Worker side of the Fetch from API step: resolves the connection and sends one GET (Worker only).</summary>
public interface IApiFetch
{
    Task<ApiFetchResult> RunAsync(ApiFetchRequest request, CancellationToken ct);
}

/// <summary>Sends the one GET with <c>HttpClient</c> (Infrastructure, Worker only): https only, no redirects, no blocked addresses, the host fixed by the connection.</summary>
public interface IApiFetchSender
{
    Task<ApiFetchResult> SendAsync(ApiConnectionSettings settings, ApiFetchRequest request, CancellationToken ct);
}

/// <summary>Verso's own wording for the Fetch from API codes: the new ones here, the connection ones as the connection test says them.</summary>
public static class ApiFetchMessages
{
    public static string For(string code) => code switch
    {
        "api.not_json" => "The API answered with something that is not JSON.",
        "api.response_too_large" => "The API's answer is larger than the step allows. Raise the limit or ask the API for less.",
        "api.bad_request" => "The request path or query is not valid.",
        "api.client_error" => "The API rejected the request (an HTTP 4xx answer). Check the path and its query values.", // the probe's text names the test path
        "api.unauthorized" or "api.rate_limited" or "api.redirect_not_followed" or "api.server_error" or "api.timeout" or "api.unreachable"
            or "api.certificate_untrusted" or "api.blocked_address" or "api.host_not_allowed" or "api.token_failed" or "api.secret_unreadable"
            or "api.connection_unavailable" => ApiProbeMessages.For(code),
        _ => "The API call could not be made.",
    };
}

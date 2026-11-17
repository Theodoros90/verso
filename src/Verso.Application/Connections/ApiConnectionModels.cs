using System.Text.RegularExpressions;

namespace Verso.Application.Connections;

/// <summary>
/// Creates or updates an API connection. <see cref="Key"/> is used on create only. <see cref="Secret"/>: null keeps the stored one, a value
/// replaces it; <see cref="ClearSecret"/> removes it. The secret is never returned.
/// </summary>
public sealed record SaveApiConnectionRequest(
    string? Key, string? Name, string? BaseUrl, string? TestPath, string? AuthType, string? AuthName, string? TokenUrl, string? Scope,
    int? TimeoutSeconds, string? Secret, bool? ClearSecret);

/// <summary>An API connection as the Configuration screen sees it. No secret: only whether one is stored.</summary>
public sealed record ApiConnectionDto(
    Guid Id, string Key, string Name, string BaseUrl, string TestPath, string AuthType, string AuthName, string TokenUrl, string Scope,
    int TimeoutSeconds, bool IsActive, bool HasSecret, DateTimeOffset UpdatedAt, string UpdatedBy);

/// <summary>The state of an API connection test. Never carries the response body, headers, the URL or a secret.</summary>
public sealed record ApiConnectionTestDto(
    Guid Id, string ConnectionKey, string Status, string? Code, string? Message, int? HttpStatus, int? ElapsedMs, string? ContentType,
    long? ResponseBytes, DateTimeOffset RequestedAt, DateTimeOffset? CompletedAt);

/// <summary>What the Worker needs to call the API, secret decrypted in memory only.</summary>
public sealed record ApiConnectionSettings(
    string BaseUrl, string TestPath, string AuthType, string AuthName, string Secret, string TokenUrl, string Scope, TimeSpan Timeout);

/// <summary>The outcome of one test request. The body is never kept: only the status, the time, the content type and the size.</summary>
public sealed record ApiProbeResult(bool Ok, string? Code, string? Message, int? HttpStatus, int? ElapsedMs, string? ContentType, long? ResponseBytes)
{
    public static ApiProbeResult Success(int httpStatus, int elapsedMs, string? contentType, long bytes) => new(true, null, null, httpStatus, elapsedMs, contentType, bytes);

    /// <summary>A failure with Verso's own wording for <paramref name="code"/>.</summary>
    public static ApiProbeResult Failure(string code, int? httpStatus = null, int? elapsedMs = null, string? contentType = null, long? bytes = null) =>
        new(false, code, ApiProbeMessages.For(code), httpStatus, elapsedMs, contentType, bytes);
}

/// <summary>
/// Sends the one GET of "Test connection" (ADR 0017), after asking the token endpoint first for OAuth. Implemented in Infrastructure with
/// <c>HttpClient</c>, registered in the Worker only. HTTPS only, no redirects, no loopback or link-local address, never keeps the body.
/// </summary>
public interface IApiProbe
{
    Task<ApiProbeResult> ProbeAsync(ApiConnectionSettings settings, CancellationToken ct);
}

/// <summary>Queues the Worker job that runs an API connection test (Hangfire in Infrastructure).</summary>
public interface IApiConnectionTestJobs
{
    void Enqueue(Guid testId);
}

/// <summary>Verso's own wording for each failure code (never the API's response text).</summary>
public static class ApiProbeMessages
{
    public static string For(string code) => code switch
    {
        "api.unauthorized" => "The API refused the credentials (HTTP 401 or 403). Check the key, token or login.",
        "api.not_found" => "The API answered 'not found' (HTTP 404). Check the base address and the test path.",
        "api.rate_limited" => "The API is limiting requests (HTTP 429). Try again later.",
        "api.redirect_not_followed" => "The API answered with a redirect, which Verso does not follow. Use the address it redirects to.",
        "api.client_error" => "The API rejected the request (an HTTP 4xx answer). Check the test path and its parameters.",
        "api.server_error" => "The API reported an error on its side (an HTTP 5xx answer).",
        "api.timeout" => "The API did not answer in time.",
        "api.unreachable" => "The API could not be reached. Check the address, the network, the firewall and any proxy.",
        "api.certificate_untrusted" => "The API's certificate is not trusted. Install its certificate authority on the Worker server.",
        "api.blocked_address" => "The address resolves to a loopback or link-local address, which Verso does not connect to.",
        "api.host_not_allowed" => "The administrator has not allowed the Worker to call this host (ApiConnections:AllowedHosts).",
        "api.token_failed" => "The token request failed (the token endpoint refused the client id or secret, or sent no access token).",
        "api.secret_unreadable" => "The stored secret can't be read: the Worker's Secrets:MasterKey is missing or differs from the API's. Set it, or type the secret again.",
        "api.connection_unavailable" => "The connection no longer exists or is switched off.",
        _ => "The test could not be run.",
    };
}

/// <summary>Optional limit set by the administrator on the Worker: when <see cref="AllowedHosts"/> is not empty only these hosts may be called.</summary>
public sealed class ApiConnectionOptions
{
    public const string Section = "ApiConnections";
    public string[] AllowedHosts { get; set; } = [];
}

public static class ApiConnectionContexts
{
    public static string For(string key) => $"api-connection:{key}";
}

public static class ApiAuthTypes
{
    public const string None = "None";
    public const string ApiKeyHeader = "ApiKeyHeader";
    public const string ApiKeyQuery = "ApiKeyQuery";
    public const string Bearer = "Bearer";
    public const string Basic = "Basic";
    public const string OAuthClientCredentials = "OAuthClientCredentials";

    public static readonly IReadOnlyList<string> All = [None, ApiKeyHeader, ApiKeyQuery, Bearer, Basic, OAuthClientCredentials];

    public static bool IsValid(string? type) => type is not null && All.Contains(type, StringComparer.Ordinal);

    public static bool NeedsSecret(string type) => type != None;

    /// <summary>Whether <c>AuthName</c> is used: the header or parameter name, the user name, or the client id.</summary>
    public static bool NeedsName(string type) => type is ApiKeyHeader or ApiKeyQuery or Basic or OAuthClientCredentials;
}

/// <summary>Address rules for API connections: https only, no credentials in the address, no query on the base.</summary>
public static partial class ApiUrls
{
    public const int MaxUrlLength = 500;

    /// <summary>An absolute https address with a host, without user info, query or fragment (the query belongs to the test path).</summary>
    public static bool IsValidBaseUrl(string? url) =>
        Parse(url) is { } uri && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    /// <summary>An absolute https address with a host, without user info or fragment (a token endpoint).</summary>
    public static bool IsValidTokenUrl(string? url) => Parse(url) is { } uri && string.IsNullOrEmpty(uri.Fragment);

    /// <summary>Starts with a single "/", has no spaces, control characters or backslashes, and can't turn into another host.</summary>
    public static bool IsValidTestPath(string? path) =>
        path is { Length: > 0 and <= MaxUrlLength } && path[0] == '/' && !path.StartsWith("//", StringComparison.Ordinal) && TestPathRegex().IsMatch(path);

    /// <summary>The host of a valid address (lower-case), for the allowed-hosts check.</summary>
    public static string? HostOf(string? url) => Parse(url)?.Host.ToLowerInvariant();

    /// <summary>The full address a test calls: the base without a trailing slash plus the test path.</summary>
    public static string Combine(string baseUrl, string testPath) => baseUrl.TrimEnd('/') + testPath;

    private static Uri? Parse(string? url) =>
        url is { Length: > 0 and <= MaxUrlLength } && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0 && string.IsNullOrEmpty(uri.UserInfo) && url.Trim().All(c => !char.IsWhiteSpace(c) && !char.IsControl(c) && c != '\\')
            ? uri
            : null;

    [GeneratedRegex(@"^/[^\s\\\x00-\x1F\x7F]*$", RegexOptions.CultureInvariant)]
    private static partial Regex TestPathRegex();
}

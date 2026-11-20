using Microsoft.EntityFrameworkCore;
using Verso.Application.Mail;

namespace Verso.Application.Connections;

/// <summary>
/// Worker side of the Fetch from API step: clamps the limits, re-checks the path and query, reads the saved connection (active, base host and, for OAuth,
/// token host allowed), decrypts the secret in memory only after those checks, and hands one GET to the sender. Failures are results with a code, never
/// exceptions (except cancellation); no message carries the address, a value, the answer or the secret.
/// </summary>
public sealed class ApiFetchService(IVersoDbContext db, ISecretProtector secrets, IApiFetchSender sender, ApiConnectionOptions options) : IApiFetch
{
    public const int MinResponseBytes = 1024;
    public const int MaxResponseBytesLimit = 256 * 1024;
    public const int MaxTimeoutSeconds = 60;
    public const int MaxQueryValues = 20;

    public async Task<ApiFetchResult> RunAsync(ApiFetchRequest request, CancellationToken ct)
    {
        // Defence in depth: whatever the caller asked for, 1 KB..256 KB and 1..60 seconds (out of range is clamped, not refused).
        var timeout = TimeSpan.FromSeconds(Math.Clamp(request.Timeout.TotalSeconds, 1, MaxTimeoutSeconds));
        request = request with
        {
            MaxResponseBytes = Math.Clamp(request.MaxResponseBytes, MinResponseBytes, MaxResponseBytesLimit),
            Timeout = timeout,
        };

        if (!IsValidPath(request.Path) || !IsValidQuery(request.Query))
        {
            return ApiFetchResult.Failure("api.bad_request", retryable: false);
        }

        var key = request.ConnectionKey;
        var connection = await db.ApiConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Key == key, ct);
        if (connection is null || !connection.IsActive)
        {
            return ApiFetchResult.Failure("api.connection_unavailable", retryable: false);
        }

        // A stored address the save rules would refuse is never used, whatever AllowedHosts says (the token endpoint receives the client id and secret).
        if (!ApiUrls.IsValidBaseUrl(connection.BaseUrl)
            || (connection.AuthType == ApiAuthTypes.OAuthClientCredentials && !ApiUrls.IsValidTokenUrl(connection.TokenUrl)))
        {
            return ApiFetchResult.Failure("api.bad_request", retryable: false);
        }

        if (!HostsAllowed(connection))
        {
            return ApiFetchResult.Failure("api.host_not_allowed", retryable: false);
        }

        if (connection.AuthType == ApiAuthTypes.ApiKeyQuery && request.Query.Any(p => string.Equals(p.Key, connection.AuthName, StringComparison.OrdinalIgnoreCase)))
        {
            return ApiFetchResult.Failure("api.bad_request", retryable: false); // a value must not stand in for (or beside) the connection's key
        }

        // Only now, with the host known to be allowed, is the secret decrypted (in memory only).
        var secret = "";
        if (connection.SecretProtected is not null)
        {
            secret = secrets.Unprotect(connection.SecretProtected, ApiConnectionContexts.For(connection.Key)) ?? "";
            if (secret.Length == 0)
            {
                return ApiFetchResult.Failure("api.secret_unreadable", retryable: false);
            }
        }

        var settings = new ApiConnectionSettings(
            connection.BaseUrl, connection.TestPath, connection.AuthType, connection.AuthName, secret, connection.TokenUrl, connection.Scope, timeout);
        try
        {
            return await sender.SendAsync(settings, request, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ApiFetchResult.Failure("api.timeout", retryable: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ApiFetchResult.Failure("api.unreachable", retryable: true); // a sender should not throw; if it does, say nothing about why
        }
    }

    /// <summary>
    /// The connection test's path rules (single leading "/", no spaces, control characters or backslash), plus: no "?" or "#" (the query goes in
    /// <see cref="ApiFetchRequest.Query"/>), and no "." or ".." segment, even percent-encoded (it would climb out of the connection's base path).
    /// </summary>
    internal static bool IsValidPath(string? path)
    {
        if (!ApiUrls.IsValidTestPath(path) || path!.Contains('?', StringComparison.Ordinal) || path.Contains('#', StringComparison.Ordinal))
        {
            return false;
        }

        return path.Split('/').All(segment => Uri.UnescapeDataString(segment) is not ("." or ".."));
    }

    private static bool IsValidQuery(IReadOnlyList<KeyValuePair<string, string>>? query) =>
        query is not null && query.Count <= MaxQueryValues && query.All(p => !string.IsNullOrEmpty(p.Key) && p.Value is not null);

    private bool HostsAllowed(Domain.Connections.ApiConnection connection)
    {
        if (options.AllowedHosts.Length == 0)
        {
            return true;
        }

        var hosts = new[] { ApiUrls.HostOf(connection.BaseUrl), connection.AuthType == ApiAuthTypes.OAuthClientCredentials ? ApiUrls.HostOf(connection.TokenUrl) : null };
        if (connection.AuthType == ApiAuthTypes.OAuthClientCredentials && hosts[1] is null)
        {
            return false; // OAuth without a valid token address: nothing to allow
        }

        return hosts.Where(h => h is not null).All(h => options.AllowedHosts.Contains(h!, StringComparer.OrdinalIgnoreCase));
    }
}

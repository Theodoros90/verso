namespace Verso.Domain.Connections;

/// <summary>
/// An HTTP API connection created in Configuration, Connections (ADR 0017). Deactivated, never deleted. The secret (API key, token,
/// password or client secret) is never held in clear: <see cref="SecretProtected"/> is the encrypted text (or null).
/// Field rules are enforced by the service.
/// </summary>
public sealed class ApiConnection
{
    private ApiConnection() { } // EF Core

    public Guid Id { get; private set; }
    public string Key { get; private set; } = "";
    public string Name { get; private set; } = "";
    /// <summary>The API's base address, https only, no query (for example <c>https://api.frankfurter.dev/v1</c>).</summary>
    public string BaseUrl { get; private set; } = "";
    /// <summary>The path and query of the request "Test connection" sends, starting with "/" (for example <c>/latest?base=EUR</c>).</summary>
    public string TestPath { get; private set; } = "";
    /// <summary>None, ApiKeyHeader, ApiKeyQuery, Bearer, Basic or OAuthClientCredentials.</summary>
    public string AuthType { get; private set; } = "None";
    /// <summary>The header or query parameter name (API key), the user name (Basic) or the client id (OAuth).</summary>
    public string AuthName { get; private set; } = "";
    /// <summary>OAuth only: where the access token is requested.</summary>
    public string TokenUrl { get; private set; } = "";
    public string Scope { get; private set; } = "";
    public int TimeoutSeconds { get; private set; }
    public bool IsActive { get; private set; }
    public string? SecretProtected { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset UpdatedAt { get; private set; }
    public string UpdatedBy { get; private set; } = "";

    public static ApiConnection Create(
        string key, string name, string baseUrl, string testPath, string authType, string authName, string tokenUrl, string scope,
        int timeoutSeconds, string actor, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        Key = key,
        Name = name.Trim(),
        BaseUrl = baseUrl.Trim(),
        TestPath = testPath.Trim(),
        AuthType = authType,
        AuthName = authName.Trim(),
        TokenUrl = tokenUrl.Trim(),
        Scope = scope.Trim(),
        TimeoutSeconds = timeoutSeconds,
        IsActive = true,
        CreatedAt = now,
        CreatedBy = actor,
        UpdatedAt = now,
        UpdatedBy = actor,
    };

    public void Update(
        string name, string baseUrl, string testPath, string authType, string authName, string tokenUrl, string scope,
        int timeoutSeconds, string actor, DateTimeOffset now)
    {
        Name = name.Trim();
        BaseUrl = baseUrl.Trim();
        TestPath = testPath.Trim();
        AuthType = authType;
        AuthName = authName.Trim();
        TokenUrl = tokenUrl.Trim();
        Scope = scope.Trim();
        TimeoutSeconds = timeoutSeconds;
        Touch(actor, now);
    }

    /// <summary>Stores the encrypted secret, or removes it with null.</summary>
    public void SetSecret(string? protectedSecret, string actor, DateTimeOffset now)
    {
        SecretProtected = protectedSecret;
        Touch(actor, now);
    }

    public void Deactivate(string actor, DateTimeOffset now)
    {
        IsActive = false;
        Touch(actor, now);
    }

    public void Activate(string actor, DateTimeOffset now)
    {
        IsActive = true;
        Touch(actor, now);
    }

    private void Touch(string actor, DateTimeOffset now)
    {
        UpdatedAt = now;
        UpdatedBy = actor;
    }
}

public enum ApiConnectionTestStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
}

/// <summary>
/// A request to test a saved API connection. The Worker sends one GET to the test path (and, for OAuth, asks the token endpoint first).
/// The response body is never kept: only the HTTP status, the time, the content type and the size. Rows are deleted after 24 hours.
/// </summary>
public sealed class ApiConnectionTest
{
    public const int MaxMessageLength = 500;

    private ApiConnectionTest() { } // EF Core

    public Guid Id { get; private set; }
    public Guid ConnectionId { get; private set; }
    public string ConnectionKey { get; private set; } = "";
    public ApiConnectionTestStatus Status { get; private set; }
    public string? Code { get; private set; }
    public string? Message { get; private set; }
    public int? HttpStatus { get; private set; }
    public int? ElapsedMs { get; private set; }
    public string? ContentType { get; private set; }
    public long? ResponseBytes { get; private set; }
    public string RequestedBy { get; private set; } = "";
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsFinished => Status is ApiConnectionTestStatus.Succeeded or ApiConnectionTestStatus.Failed;

    public static ApiConnectionTest Create(Guid connectionId, string connectionKey, string actor, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        ConnectionId = connectionId,
        ConnectionKey = connectionKey,
        Status = ApiConnectionTestStatus.Queued,
        RequestedBy = actor,
        RequestedAt = now,
    };

    public void Start() => Status = ApiConnectionTestStatus.Running;

    public void Succeed(int? httpStatus, int? elapsedMs, string? contentType, long? responseBytes, DateTimeOffset now)
    {
        Status = ApiConnectionTestStatus.Succeeded;
        Record(httpStatus, elapsedMs, contentType, responseBytes);
        CompletedAt = now;
    }

    public void Fail(string code, string message, int? httpStatus, int? elapsedMs, string? contentType, long? responseBytes, DateTimeOffset now)
    {
        Status = ApiConnectionTestStatus.Failed;
        Code = code.Length > 64 ? code[..64] : code;
        Message = message.Length > MaxMessageLength ? message[..MaxMessageLength] : message;
        Record(httpStatus, elapsedMs, contentType, responseBytes);
        CompletedAt = now;
    }

    private void Record(int? httpStatus, int? elapsedMs, string? contentType, long? responseBytes)
    {
        HttpStatus = httpStatus;
        ElapsedMs = elapsedMs;
        ContentType = contentType is { Length: > 100 } ? contentType[..100] : contentType;
        ResponseBytes = responseBytes;
    }
}

namespace Verso.Domain.Connections;

/// <summary>
/// A SQL Server connection created in Configuration, Connections (ADR 0016). Connections are deactivated, never deleted.
/// The password is never held in clear: <see cref="PasswordProtected"/> is the encrypted text (or null). Field rules are enforced by the service.
/// </summary>
public sealed class DatabaseConnection
{
    private DatabaseConnection() { } // EF Core

    public Guid Id { get; private set; }
    public string Key { get; private set; } = "";
    /// <summary>SqlServer, PostgreSql, MySql or Oracle. Never changes after creation.</summary>
    public string Provider { get; private set; } = "SqlServer";
    public string Name { get; private set; } = "";
    /// <summary>Host name. SQL Server also accepts an instance (<c>host\INSTANCE</c>) or a port (<c>host,1433</c>).</summary>
    public string Server { get; private set; } = "";
    /// <summary>0 means the provider's default port.</summary>
    public int Port { get; private set; }
    /// <summary>Initial database; empty means the login's default. For Oracle this is the service name (required).</summary>
    public string Database { get; private set; } = "";
    public string UserName { get; private set; } = "";
    public bool Encrypt { get; private set; }
    public bool TrustServerCertificate { get; private set; }
    public int TimeoutSeconds { get; private set; }
    public bool IsActive { get; private set; }
    public string? PasswordProtected { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset UpdatedAt { get; private set; }
    public string UpdatedBy { get; private set; } = "";

    public static DatabaseConnection Create(
        string key, string provider, string name, string server, int port, string database, string userName, bool encrypt, bool trustServerCertificate,
        int timeoutSeconds, string actor, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        Key = key,
        Provider = provider,
        Name = name.Trim(),
        Server = server.Trim(),
        Port = port,
        Database = database.Trim(),
        UserName = userName.Trim(),
        Encrypt = encrypt,
        TrustServerCertificate = trustServerCertificate,
        TimeoutSeconds = timeoutSeconds,
        IsActive = true,
        CreatedAt = now,
        CreatedBy = actor,
        UpdatedAt = now,
        UpdatedBy = actor,
    };

    public void Update(
        string name, string server, int port, string database, string userName, bool encrypt, bool trustServerCertificate,
        int timeoutSeconds, string actor, DateTimeOffset now)
    {
        Name = name.Trim();
        Server = server.Trim();
        Port = port;
        Database = database.Trim();
        UserName = userName.Trim();
        Encrypt = encrypt;
        TrustServerCertificate = trustServerCertificate;
        TimeoutSeconds = timeoutSeconds;
        Touch(actor, now);
    }

    /// <summary>Stores the encrypted password, or removes it with null.</summary>
    public void SetPassword(string? protectedPassword, string actor, DateTimeOffset now)
    {
        PasswordProtected = protectedPassword;
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

public enum DatabaseConnectionTestStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
}

/// <summary>A request to test a saved database connection. The Worker connects and runs a fixed <c>SELECT 1</c> only. Rows are deleted after 24 hours.</summary>
public sealed class DatabaseConnectionTest
{
    public const int MaxMessageLength = 500;

    private DatabaseConnectionTest() { } // EF Core

    public Guid Id { get; private set; }
    public Guid ConnectionId { get; private set; }
    public string ConnectionKey { get; private set; } = "";
    public DatabaseConnectionTestStatus Status { get; private set; }
    /// <summary>The error code when the test failed (for example <c>db.login_failed</c>).</summary>
    public string? Code { get; private set; }
    public string? Message { get; private set; }
    /// <summary>The SQL Server version the connection reported, when the test succeeded.</summary>
    public string? ServerVersion { get; private set; }
    public string RequestedBy { get; private set; } = "";
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsFinished => Status is DatabaseConnectionTestStatus.Succeeded or DatabaseConnectionTestStatus.Failed;

    public static DatabaseConnectionTest Create(Guid connectionId, string connectionKey, string actor, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        ConnectionId = connectionId,
        ConnectionKey = connectionKey,
        Status = DatabaseConnectionTestStatus.Queued,
        RequestedBy = actor,
        RequestedAt = now,
    };

    public void Start() => Status = DatabaseConnectionTestStatus.Running;

    public void Succeed(string? serverVersion, DateTimeOffset now)
    {
        Status = DatabaseConnectionTestStatus.Succeeded;
        ServerVersion = serverVersion is { Length: > 100 } ? serverVersion[..100] : serverVersion;
        CompletedAt = now;
    }

    public void Fail(string code, string message, DateTimeOffset now)
    {
        Status = DatabaseConnectionTestStatus.Failed;
        Code = code.Length > 64 ? code[..64] : code;
        Message = message.Length > MaxMessageLength ? message[..MaxMessageLength] : message;
        CompletedAt = now;
    }
}

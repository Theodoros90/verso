using Verso.Application.Executions;
using Verso.Application.Mail;

namespace Verso.Application.Connections;

/// <summary>
/// Worker side of the Query database step: re-checks the text with <see cref="SqlGuard"/>, reads the saved connection, decrypts the password in memory and
/// hands the query to the provider's runner. Failures are results with a code, never exceptions (except cancellation); parameter values never leave in a message.
/// </summary>
public sealed class DatabaseQueryService(IVersoDbContext db, ISecretProtector secrets, IEnumerable<IProviderQueryRunner> runners, DatabaseConnectionOptions options) : IDatabaseQuery
{
    private readonly Dictionary<string, IProviderQueryRunner> _runners = runners.ToDictionary(r => r.Provider, StringComparer.Ordinal);
    private readonly DatabaseConnectionResolver _resolver = new(db, secrets, options); // shared with the Write to database step

    public const int MaxRowsLimit = 100;
    public const int MaxTimeoutSeconds = 60;

    public async Task<DatabaseQueryResult> RunAsync(DatabaseQueryRequest request, CancellationToken ct)
    {
        // Defence in depth: whatever the caller asked for, a query returns 1..100 rows and runs 1..60 seconds (out of range is clamped, not refused).
        request = request with
        {
            MaxRows = Math.Clamp(request.MaxRows, 1, MaxRowsLimit),
            Timeout = TimeSpan.FromSeconds(Math.Clamp(request.Timeout.TotalSeconds, 1, MaxTimeoutSeconds)),
        };

        var check = SqlGuard.Check(request.Sql);
        if (!check.Ok)
        {
            return DatabaseQueryResult.Failure(check.Code ?? "sql.unsupported_syntax", false);
        }

        // Every placeholder needs a value (a missing one is the author's mistake, not a database error).
        var supplied = request.Parameters.Keys.Select(k => k.TrimStart('@')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (check.Parameters.Any(p => !supplied.Contains(p)))
        {
            return DatabaseQueryResult.Failure("sql.bad_parameter", false);
        }

        var (settings, _, code) = await _resolver.ResolveAsync(request.ConnectionKey, requireWrites: false, ct);
        if (settings is null)
        {
            return DatabaseQueryResult.Failure(code ?? "db.connection_unavailable", false);
        }

        if (!_runners.TryGetValue(settings.Provider, out var runner))
        {
            return DatabaseQueryResult.Failure("db.provider_unavailable", false);
        }

        try
        {
            return await runner.RunAsync(settings, request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DatabaseQueryResult.Failure("db.query_failed", false); // a runner should not throw; if it does, say nothing about why
        }
    }
}

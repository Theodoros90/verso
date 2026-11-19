using Verso.Application.Executions;

namespace Verso.Application.Connections;

/// <summary>
/// Worker side of the Write to database step: clamps the limits, re-checks the text with <see cref="SqlGuard.CheckWrite"/>, checks every placeholder has a value,
/// resolves the saved connection (refused with <c>db.writes_not_allowed</c> before its password is decrypted when "Allow writes" is off) and hands the statement to
/// the provider's runner. Failures are results with a code, never exceptions (except cancellation); values and the SQL never leave in a message. Nothing is retried.
/// </summary>
public sealed class DatabaseWriteService(DatabaseConnectionResolver resolver, IEnumerable<IProviderWriteRunner> runners) : IDatabaseWrite
{
    private readonly Dictionary<string, IProviderWriteRunner> _runners = runners.ToDictionary(r => r.Provider, StringComparer.Ordinal);

    public const int MaxRowsLimit = 1000;
    public const int MaxTimeoutSeconds = 60;

    public async Task<DatabaseWriteResult> RunAsync(DatabaseWriteRequest request, CancellationToken ct)
    {
        // Defence in depth: whatever the caller asked for, a write may change 1..1000 rows and runs 1..60 seconds (out of range is clamped, not refused).
        request = request with
        {
            MaxRows = Math.Clamp(request.MaxRows, 1, MaxRowsLimit),
            Timeout = TimeSpan.FromSeconds(Math.Clamp(request.Timeout.TotalSeconds, 1, MaxTimeoutSeconds)),
        };

        var check = SqlGuard.CheckWrite(request.Sql);
        if (!check.Ok)
        {
            return DatabaseWriteResult.Failure(check.Code ?? "sql.unsupported_syntax", false);
        }

        // Every placeholder needs a value (a missing one is the author's mistake, not a database error).
        var supplied = request.Parameters.Keys.Select(k => k.TrimStart('@')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (check.Parameters.Any(p => !supplied.Contains(p)))
        {
            return DatabaseWriteResult.Failure("sql.bad_parameter", false);
        }

        var (settings, code) = await resolver.ResolveAsync(request.ConnectionKey, requireWrites: true, ct);
        if (settings is null)
        {
            return DatabaseWriteResult.Failure(code ?? "db.connection_unavailable", false);
        }

        if (!_runners.TryGetValue(settings.Provider, out var runner))
        {
            return DatabaseWriteResult.Failure("db.provider_unavailable", false);
        }

        try
        {
            return await runner.RunAsync(settings, request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DatabaseWriteResult.Failure("db.query_failed", true); // a runner should not throw; if it does, it may have written: say nothing about why
        }
    }
}

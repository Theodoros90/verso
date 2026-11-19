using Verso.Application.Connections;

namespace Verso.Api.Tests;

/// <summary>
/// Stands in for the database behind a Write to database step: answers with the result it is given (a success with a row count by default, or a failure a set
/// number of times), or throws; records every request, parameters included, so tests can count how often a write would have been sent. Never connects to anything.
/// </summary>
internal sealed class FakeDatabaseWrite : IDatabaseWrite
{
    private readonly List<DatabaseWriteRequest> _requests = [];
    private int _failed;

    /// <summary>Rows changed by every successful call.</summary>
    public int RowsAffected { get; set; } = 1;

    /// <summary>Returned instead of a success while <see cref="FailTimes"/> calls have not failed yet.</summary>
    public DatabaseWriteResult? Failure { get; set; }

    public int FailTimes { get; set; } = int.MaxValue;

    /// <summary>When set, every call throws it (a service that broke its contract of returning failures as results, or a cancellation).</summary>
    public Exception? Throw { get; set; }

    /// <summary>Called during each call (after it was recorded) with the 1-based call number and the token; may cancel the Worker's shutdown token.</summary>
    public Action<int, CancellationToken>? During { get; set; }

    public IReadOnlyList<DatabaseWriteRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public int CallCount => Requests.Count;

    public Task<DatabaseWriteResult> RunAsync(DatabaseWriteRequest request, CancellationToken ct)
    {
        int call;
        lock (_requests)
        {
            // A copy: the caller's dictionary must not change what was recorded.
            _requests.Add(request with { Parameters = new Dictionary<string, object?>(request.Parameters, StringComparer.Ordinal) });
            call = _requests.Count;
        }

        During?.Invoke(call, ct);
        ct.ThrowIfCancellationRequested(); // as the real service: a cancel while the statement runs surfaces as a cancellation
        if (Throw is not null)
        {
            throw Throw;
        }

        if (Failure is not null && _failed < FailTimes)
        {
            _failed++;
            return Task.FromResult(Failure);
        }

        return Task.FromResult(DatabaseWriteResult.Success(RowsAffected, 9));
    }
}

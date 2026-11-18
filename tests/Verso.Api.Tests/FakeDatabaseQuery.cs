using Verso.Application.Connections;

namespace Verso.Api.Tests;

/// <summary>
/// Stands in for the database behind a Query database step: returns the rows it is given (or a failure, a set number of times), and
/// records every request, parameters included, so tests can check what would have been sent. Never connects to anything.
/// </summary>
internal sealed class FakeDatabaseQuery : IDatabaseQuery
{
    private readonly List<DatabaseQueryRequest> _requests = [];
    private int _failed;

    /// <summary>The rows every successful call returns (empty: no row found).</summary>
    public List<IReadOnlyDictionary<string, object?>> Rows { get; set; } = [];

    /// <summary>The column names returned; when null, the keys of the first row.</summary>
    public IReadOnlyList<string>? Columns { get; set; }

    public bool Truncated { get; set; }

    /// <summary>Returned instead of rows while <see cref="FailTimes"/> calls have not failed yet.</summary>
    public DatabaseQueryResult? Failure { get; set; }

    public int FailTimes { get; set; } = int.MaxValue;

    /// <summary>When set, every call throws it (a service that broke its contract of returning failures as results).</summary>
    public Exception? Throw { get; set; }

    /// <summary>Called before each answer with the 1-based call number; may change <see cref="Rows"/> (e.g. a database that changed meanwhile).</summary>
    public Action<int>? BeforeAnswer { get; set; }

    public IReadOnlyList<DatabaseQueryRequest> Requests
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

    public static IReadOnlyDictionary<string, object?> Row(params (string Column, object? Value)[] cells) =>
        cells.ToDictionary(c => c.Column, c => c.Value, StringComparer.Ordinal);

    public Task<DatabaseQueryResult> RunAsync(DatabaseQueryRequest request, CancellationToken ct)
    {
        int call;
        lock (_requests)
        {
            // A copy: the caller's dictionary must not change what was recorded.
            _requests.Add(request with { Parameters = new Dictionary<string, object?>(request.Parameters, StringComparer.Ordinal) });
            call = _requests.Count;
        }

        BeforeAnswer?.Invoke(call);
        if (Throw is not null)
        {
            throw Throw;
        }

        if (Failure is not null && _failed < FailTimes)
        {
            _failed++;
            return Task.FromResult(Failure);
        }

        var columns = Columns ?? (Rows.Count > 0 ? [.. Rows[0].Keys] : []);
        return Task.FromResult(DatabaseQueryResult.Success(columns, [.. Rows], Truncated, 7));
    }
}

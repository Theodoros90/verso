using System.Text;
using System.Text.Json;
using Verso.Application.Connections;

namespace Verso.Api.Tests;

/// <summary>
/// Stands in for the API behind a Fetch from API step: returns the result it is given (or a failure, a set number of times, or throws), and records every
/// request (connection, resolved path, raw query pairs, limits) so tests can check what would have been sent. Never connects to anything.
/// </summary>
internal sealed class FakeApiFetch : IApiFetch
{
    private readonly List<ApiFetchRequest> _requests = [];
    private int _failed;

    /// <summary>What every successful call returns. By default a 200 with a small JSON object.</summary>
    public ApiFetchResult Result { get; set; } = Answer("""{ "name": "Fake API answer" }""");

    /// <summary>Returned instead of <see cref="Result"/> while <see cref="FailTimes"/> calls have not failed yet.</summary>
    public ApiFetchResult? Failure { get; set; }

    public int FailTimes { get; set; } = int.MaxValue;

    /// <summary>When set, every call throws it (a service that broke its contract of returning failures as results).</summary>
    public Exception? Throw { get; set; }

    /// <summary>Called before each answer with the 1-based call number; may change <see cref="Result"/> or <see cref="Failure"/> (an API that changed meanwhile).</summary>
    public Action<int>? BeforeAnswer { get; set; }

    public IReadOnlyList<ApiFetchRequest> Requests
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

    /// <summary>A 2xx answer with <paramref name="json"/> as its parsed body (the element owns its memory, as the real sender's does).</summary>
    public static ApiFetchResult Answer(string json, int status = 200)
    {
        using var document = JsonDocument.Parse(json);
        return ApiFetchResult.Success(status, document.RootElement.Clone(), Encoding.UTF8.GetByteCount(json), 9);
    }

    /// <summary>A success without a body: HTTP 404, or an empty 204.</summary>
    public static ApiFetchResult Nothing(int status = 404) => ApiFetchResult.Success(status, null, 0, 4);

    public Task<ApiFetchResult> RunAsync(ApiFetchRequest request, CancellationToken ct)
    {
        int call;
        lock (_requests)
        {
            // A copy: the caller's list must not change what was recorded.
            _requests.Add(request with { Query = [.. request.Query] });
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

        return Task.FromResult(Result);
    }
}

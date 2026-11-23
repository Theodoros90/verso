using Verso.Application.Connections;

namespace Verso.Api.Tests;

/// <summary>
/// Stands in for the SMS provider behind a Send SMS step: answers with the result it is given (a success with a message id by default, or a failure a set number
/// of times), or throws; records every request (connection, digits, text, tag) so tests can count how often a message would have been sent. Never sends anything.
/// </summary>
internal sealed class FakeSmsSend : ISmsSend
{
    private readonly List<SmsSendRequest> _requests = [];
    private int _failed;

    /// <summary>Returned instead of a success while <see cref="FailTimes"/> calls have not failed yet.</summary>
    public SmsSendResult? Failure { get; set; }

    public int FailTimes { get; set; } = int.MaxValue;

    /// <summary>When set, every call throws it (a service that broke its contract of returning failures as results, or a cancellation).</summary>
    public Exception? Throw { get; set; }

    /// <summary>Called during each call (after it was recorded) with the 1-based call number and the token; may cancel the Worker's shutdown token.</summary>
    public Action<int, CancellationToken>? During { get; set; }

    public IReadOnlyList<SmsSendRequest> Requests
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

    public Task<SmsSendResult> RunAsync(SmsSendRequest request, CancellationToken ct)
    {
        int call;
        lock (_requests)
        {
            _requests.Add(request);
            call = _requests.Count;
        }

        During?.Invoke(call, ct);
        ct.ThrowIfCancellationRequested(); // as the real service: a cancel while the request is out surfaces as a cancellation
        if (Throw is not null)
        {
            throw Throw;
        }

        if (Failure is not null && _failed < FailTimes)
        {
            _failed++;
            return Task.FromResult(Failure);
        }

        return Task.FromResult(SmsSendResult.Success("4411" + call.ToString(System.Globalization.CultureInfo.InvariantCulture), 1, 7));
    }
}

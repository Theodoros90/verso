using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Verso.Api.Tests;

/// <summary>One log entry as written: category, level, message, exception text and every scope value around it.</summary>
internal sealed record CapturedLog(string Category, LogLevel Level, string Message, string? Exception, IReadOnlyDictionary<string, string> Scope)
{
    /// <summary>Everything a log sink would receive, as one string (for "does this appear anywhere" checks).</summary>
    public string AllText => $"{Category} {Message} {Exception} {string.Join(' ', Scope.Select(kv => $"{kv.Key}={kv.Value}"))}";
}

/// <summary>Records every log entry of a host, with its scopes, so tests can check what reaches the logs.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public ConcurrentQueue<CapturedLog> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var scope = new Dictionary<string, string>(StringComparer.Ordinal);
            void Add(object? value)
            {
                if (value is IEnumerable<KeyValuePair<string, object>> pairs)
                {
                    foreach (var (k, v) in pairs)
                    {
                        scope[k] = v?.ToString() ?? "";
                    }
                }
                else if (value is not null)
                {
                    scope[$"scope{scope.Count}"] = value.ToString() ?? "";
                }
            }

            owner._scopes.ForEachScope((s, _) => Add(s), (object?)null);
            if (state is IEnumerable<KeyValuePair<string, object>> fields)
            {
                foreach (var (k, v) in fields)
                {
                    scope["field:" + k] = v?.ToString() ?? "";
                }
            }

            owner.Entries.Enqueue(new CapturedLog(category, logLevel, formatter(state, exception), exception?.ToString(), scope));
        }
    }
}

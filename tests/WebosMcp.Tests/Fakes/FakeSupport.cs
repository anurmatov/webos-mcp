using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using WebosMcp.Application;

namespace WebosMcp.Tests.Fakes;

public sealed class FakeClientKeyStore : IClientKeyStore
{
    public FakeClientKeyStore(string? initial = "test-client-key") => Current = initial;

    public string? Current { get; set; }

    public List<string> Writes { get; } = [];

    public Task<string?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Current);

    public Task WriteAsync(string clientKey, CancellationToken cancellationToken)
    {
        Writes.Add(clientKey);
        Current = clientKey;
        return Task.CompletedTask;
    }

    public string DescribeLocation() => "(test key store)";
}

public sealed record WolSend(string Mac, IReadOnlyList<string> Targets);

public sealed class FakeWolSender : IWolSender
{
    public List<WolSend> Sends { get; } = [];

    public Task<IReadOnlyList<string>> SendAsync(
        PhysicalAddress mac,
        IReadOnlyList<IPEndPoint> targets,
        CancellationToken cancellationToken)
    {
        var addresses = targets.Select(t => t.ToString()).ToList();
        Sends.Add(new WolSend(mac.ToString(), addresses));
        return Task.FromResult<IReadOnlyList<string>>(addresses);
    }
}

/// <summary>Completes immediately so bounded fallback and poll loops run instantly in CI.</summary>
public sealed class InstantDelayProvider : IDelayProvider
{
    public int Count { get; private set; }

    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        Count++;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

/// <summary>Captures every log line so tests can assert a secret never appears.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentBag<string> Lines { get; } = [];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(Lines);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(ConcurrentBag<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lines.Add(formatter(state, exception) + " " + (exception?.ToString() ?? string.Empty));
        }
    }
}

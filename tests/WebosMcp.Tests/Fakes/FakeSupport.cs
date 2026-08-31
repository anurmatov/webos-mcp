using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using WebosMcp.Application;
using WebosMcp.Domain;

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

    /// <summary>Null simulates a read-only key source with no writable destination.</summary>
    public string? DurableWritablePath { get; set; } = "/test/clientkey.json";

    public List<string> Persists { get; } = [];

    public Task<string> PersistAsync(string clientKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(DurableWritablePath))
        {
            throw TvException.KeyStorageReadOnly("(test) no durable writable key location is configured.");
        }

        Persists.Add(clientKey);
        Current = clientKey;
        return Task.FromResult(DurableWritablePath!);
    }

    public string DescribeLocation() => DurableWritablePath ?? "(test read-only key source)";
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

/// <summary>
/// Stands in for the capture download. Defaults to a minimal but genuinely
/// well-formed JPEG so no test can reach the network, and so a test that forgets
/// to script a body still exercises a realistic success path.
/// </summary>
public sealed class FakeScreenshotDownloader : IScreenshotDownloader
{
    /// <summary>A three-byte JPEG signature: enough for content sniffing, and not a real image.</summary>
    public static readonly byte[] SyntheticJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];

    public static readonly byte[] SyntheticPng =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

    public List<Uri> Requested { get; } = [];

    public byte[] Body { get; set; } = SyntheticJpeg;

    /// <summary>Thrown instead of returning a body, when set.</summary>
    public Exception? Failure { get; set; }

    public Task<ReadOnlyMemory<byte>> DownloadAsync(Uri imageUri, CancellationToken cancellationToken)
    {
        Requested.Add(imageUri);

        if (Failure is not null)
        {
            throw Failure;
        }

        return Task.FromResult<ReadOnlyMemory<byte>>(Body);
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

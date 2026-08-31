using System.Collections.Concurrent;
using System.Text.Json;
using WebosMcp.Application;
using WebosMcp.Domain;

namespace WebosMcp.Tests.Fakes;

public sealed record SsapCall(string Kind, string Target, string? Payload);

/// <summary>
/// Scriptable stand-in for the SSAP WebSocket channel. Every network boundary
/// sits behind this, so the whole suite runs with no physical TV.
/// </summary>
public sealed class FakeSsapConnection : ISsapConnection
{
    private readonly ConcurrentQueue<SsapCall> _calls = new();
    private readonly Dictionary<string, Func<object?, JsonElement>> _responses = new(StringComparer.Ordinal);

    public bool IsConnected { get; private set; }
    public bool Disposed { get; private set; }
    public int ConnectCount { get; private set; }
    public int RegisterCount { get; private set; }
    public string? LastClientKey { get; private set; }

    /// <summary>Thrown by <see cref="ConnectAsync"/> when set.</summary>
    public Exception? ConnectFailure { get; set; }

    /// <summary>Thrown by <see cref="RegisterAsync"/> when set.</summary>
    public Exception? RegisterFailure { get; set; }

    /// <summary>The key the TV hands back. Defaults to echoing the supplied key.</summary>
    public string? IssuedClientKey { get; set; }

    /// <summary>URIs that should fail once, then succeed. Drives the reconnect tests.</summary>
    public Dictionary<string, Queue<Exception>> TransientFailures { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, every request awaits this token indefinitely. Drives the timeout tests.</summary>
    public bool HangForever { get; set; }

    /// <summary>Invoked around each request so a test can observe interleaving.</summary>
    public Func<string, Task>? OnRequestEnter { get; set; }

    public Func<string, Task>? OnRequestExit { get; set; }

    public IReadOnlyList<SsapCall> Calls => [.. _calls];

    public IReadOnlyList<string> RequestUris =>
        [.. _calls.Where(c => c.Kind == "request").Select(c => c.Target)];

    public void Respond(string uri, string json) =>
        _responses[uri] = _ => JsonDocument.Parse(json).RootElement.Clone();

    public void Fail(string uri, Exception exception) =>
        TransientFailures[uri] = new Queue<Exception>([exception]);

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        ConnectCount++;
        if (ConnectFailure is not null)
        {
            throw ConnectFailure;
        }

        IsConnected = true;
        return Task.CompletedTask;
    }

    /// <summary>When set, RegisterAsync never completes — drives the pairing-timeout tests.</summary>
    public bool HangOnRegister { get; set; }

    public async Task<string> RegisterAsync(string? clientKey, CancellationToken cancellationToken)
    {
        RegisterCount++;
        LastClientKey = clientKey;

        if (HangOnRegister)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }

        if (RegisterFailure is not null)
        {
            throw RegisterFailure;
        }

        return IssuedClientKey ?? clientKey ?? "issued-key";
    }

    public async Task<JsonElement> RequestAsync(string uri, object? payload, CancellationToken cancellationToken)
    {
        _calls.Enqueue(new SsapCall("request", uri, payload is null ? null : JsonSerializer.Serialize(payload)));

        if (OnRequestEnter is not null)
        {
            await OnRequestEnter(uri).ConfigureAwait(false);
        }

        try
        {
            if (HangForever)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            if (TransientFailures.TryGetValue(uri, out var queue) && queue.TryDequeue(out var failure))
            {
                // Only a genuine transport fault drops the socket. An SSAP-level
                // rejection (a refused deep link, an unsupported capability)
                // leaves the connection perfectly usable.
                if (failure is not TvException)
                {
                    IsConnected = false;
                }

                throw failure;
            }

            if (_responses.TryGetValue(uri, out var factory))
            {
                return factory(payload);
            }

            return JsonDocument.Parse("""{"returnValue":true}""").RootElement.Clone();
        }
        finally
        {
            if (OnRequestExit is not null)
            {
                await OnRequestExit(uri).ConfigureAwait(false);
            }
        }
    }

    public async Task SendButtonAsync(string wireName, CancellationToken cancellationToken)
    {
        _calls.Enqueue(new SsapCall("button", wireName, null));

        if (OnRequestEnter is not null)
        {
            await OnRequestEnter("button:" + wireName).ConfigureAwait(false);
        }

        if (OnRequestExit is not null)
        {
            await OnRequestExit("button:" + wireName).ConfigureAwait(false);
        }
    }

    public Task SendPointerMoveAsync(int deltaX, int deltaY, bool drag, CancellationToken cancellationToken)
    {
        _calls.Enqueue(new SsapCall("move", $"{deltaX},{deltaY},{(drag ? 1 : 0)}", null));
        return Task.CompletedTask;
    }

    public Task SendPointerClickAsync(CancellationToken cancellationToken)
    {
        _calls.Enqueue(new SsapCall("click", string.Empty, null));
        return Task.CompletedTask;
    }

    public Task SendPointerScrollAsync(int deltaX, int deltaY, CancellationToken cancellationToken)
    {
        _calls.Enqueue(new SsapCall("scroll", $"{deltaX},{deltaY}", null));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}

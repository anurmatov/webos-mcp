using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebosMcp.Domain;

namespace WebosMcp.Application;

public interface ITvSession
{
    /// <summary>
    /// Runs <paramref name="action"/> against a live, registered SSAP connection.
    /// Serialized per connection: two concurrent callers never interleave on the wire.
    /// </summary>
    Task<T> ExecuteAsync<T>(
        string operation,
        Func<ISsapConnection, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken);

    Task ExecuteAsync(
        string operation,
        Func<ISsapConnection, CancellationToken, Task> action,
        CancellationToken cancellationToken);

    /// <summary>True when a client key is available. Does not touch the network.</summary>
    Task<bool> IsPairedAsync(CancellationToken cancellationToken);

    /// <summary>Drops the current connection so the next call reconnects. Used after TV sleep/reboot.</summary>
    Task ResetAsync();
}

/// <summary>
/// Owns the single SSAP connection: pairing gate, serialization, bounded
/// timeouts, and transparent reconnect after the TV sleeps or reboots.
/// </summary>
public sealed class TvSession : ITvSession, IAsyncDisposable
{
    private readonly ISsapConnectionFactory _factory;
    private readonly IClientKeyStore _keyStore;
    private readonly WebosMcpOptions _options;
    private readonly ILogger<TvSession> _logger;

    // The serialization gate. Held for the whole of ExecuteAsync — including
    // connect and register — so no two callers' command sequences interleave.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ISsapConnection? _connection;

    public TvSession(
        ISsapConnectionFactory factory,
        IClientKeyStore keyStore,
        IOptions<WebosMcpOptions> options,
        ILogger<TvSession> logger)
    {
        _factory = factory;
        _keyStore = keyStore;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> IsPairedAsync(CancellationToken cancellationToken) =>
        !string.IsNullOrWhiteSpace(await _keyStore.ReadAsync(cancellationToken).ConfigureAwait(false));

    public async Task ExecuteAsync(
        string operation,
        Func<ISsapConnection, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync<object?>(
            operation,
            async (connection, ct) =>
            {
                await action(connection, ct).ConfigureAwait(false);
                return null;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> ExecuteAsync<T>(
        string operation,
        Func<ISsapConnection, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        // Fail closed BEFORE any network contact, so PAIRING_REQUIRED is never
        // masked by the TV happening to be off or unreachable.
        var clientKey = await _keyStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(clientKey))
        {
            throw TvException.PairingRequired();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));

            try
            {
                var connection = await EnsureConnectedAsync(clientKey!, timeout.Token).ConfigureAwait(false);
                try
                {
                    return await action(connection, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsTransportFailure(ex))
                {
                    // The TV slept or rebooted mid-call. Drop and retry once on a
                    // fresh connection rather than forcing a process restart.
                    _logger.LogInformation(
                        "SSAP transport failure during '{Operation}'; reconnecting once.", operation);
                    await DisposeConnectionAsync().ConfigureAwait(false);

                    var retry = await EnsureConnectedAsync(clientKey!, timeout.Token).ConfigureAwait(false);
                    return await action(retry, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The CALLER cancelled. Propagate cancellation as cancellation —
                // rewrapping it as a TvException would misreport a deliberate
                // abort as a TV fault.
                await DisposeConnectionAsync().ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException)
            {
                // Only the internal timeout fired.
                await DisposeConnectionAsync().ConfigureAwait(false);
                throw TvException.TimedOut(operation);
            }
            catch (TvException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await DisposeConnectionAsync().ConfigureAwait(false);
                throw TvException.Unreachable($"Operation '{operation}' failed: {ex.Message}", ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ISsapConnection> EnsureConnectedAsync(string clientKey, CancellationToken cancellationToken)
    {
        if (_connection is { IsConnected: true })
        {
            return _connection;
        }

        await DisposeConnectionAsync().ConfigureAwait(false);

        var connection = _factory.Create(_options.RequireEndpoint(), _options.UseTls);
        try
        {
            await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var accepted = await connection.RegisterAsync(clientKey, cancellationToken).ConfigureAwait(false);

            if (!string.Equals(accepted, clientKey, StringComparison.Ordinal))
            {
                // The TV issued a replacement key during re-registration.
                await _keyStore.WriteAsync(accepted, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _connection = connection;
        return connection;
    }

    public async Task ResetAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeConnectionAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DisposeConnectionAsync()
    {
        if (_connection is null)
        {
            return;
        }

        try
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Ignoring error while disposing SSAP connection: {Message}", ex.Message);
        }

        _connection = null;
    }

    private static bool IsTransportFailure(Exception ex) => ex switch
    {
        TvException tv => tv.Code is TvErrorCode.TvUnreachable,
        System.Net.WebSockets.WebSocketException => true,
        System.Net.Sockets.SocketException => true,
        IOException => true,
        ObjectDisposedException => true,
        _ => false,
    };

    public async ValueTask DisposeAsync()
    {
        await DisposeConnectionAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}

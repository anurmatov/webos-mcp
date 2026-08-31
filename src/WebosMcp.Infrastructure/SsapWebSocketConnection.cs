using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WebosMcp.Application;
using WebosMcp.Domain;

namespace WebosMcp.Infrastructure;

/// <summary>
/// The real SSAP WebSocket channel. Everything above this type is transport
/// agnostic and testable with no physical TV.
/// </summary>
public sealed class SsapWebSocketConnection : ISsapConnection
{
    private const int ReceiveBufferSize = 16 * 1024;
    private const int MaxMessageBytes = 4 * 1024 * 1024;

    private readonly IPEndPoint _endpoint;
    private readonly bool _useTls;
    private readonly TimeSpan _connectTimeout;
    private readonly ILogger _logger;

    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending = new(StringComparer.Ordinal);
    private readonly object _pendingLock = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ClientWebSocket? _socket;
    private ClientWebSocket? _pointerSocket;
    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoop;
    private int _nextId;
    private volatile bool _faulted;

    public SsapWebSocketConnection(IPEndPoint endpoint, bool useTls, TimeSpan connectTimeout, ILogger logger)
    {
        _endpoint = endpoint;
        _useTls = useTls;
        _connectTimeout = connectTimeout;
        _logger = logger;
    }

    public bool IsConnected => !_faulted && _socket is { State: WebSocketState.Open };

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var scheme = _useTls ? "wss" : "ws";
        var uri = new Uri($"{scheme}://{_endpoint.Address}:{_endpoint.Port}/");

        var socket = new ClientWebSocket();
        if (_useTls)
        {
            // LG TVs ship a self-signed certificate for the wss endpoint. The
            // channel is LAN-local and authenticated by the paired client key.
            socket.Options.RemoteCertificateValidationCallback =
                (_, _, _, _) => true;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_connectTimeout);

        try
        {
            await socket.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw TvException.Off(
                $"No SSAP response from {_endpoint.Address}:{_endpoint.Port} within {_connectTimeout.TotalSeconds:0}s. " +
                "The TV is most likely powered off or in standby.");
        }
        catch (Exception ex)
        {
            socket.Dispose();
            throw MapConnectFailure(ex);
        }

        _socket = socket;
        _faulted = false;
        _readLoopCts = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadLoopAsync(socket, _readLoopCts.Token), CancellationToken.None);
    }

    private TvException MapConnectFailure(Exception ex)
    {
        var socketError = ex as System.Net.Sockets.SocketException
            ?? ex.InnerException as System.Net.Sockets.SocketException;

        if (socketError is not null)
        {
            return socketError.SocketErrorCode switch
            {
                System.Net.Sockets.SocketError.ConnectionRefused or
                System.Net.Sockets.SocketError.TimedOut or
                System.Net.Sockets.SocketError.HostDown =>
                    TvException.Off(
                        $"Connection to {_endpoint.Address}:{_endpoint.Port} was refused or timed out — the TV is most likely off or in standby."),
                _ => TvException.Unreachable(
                    $"Could not reach the TV at {_endpoint.Address}:{_endpoint.Port} ({socketError.SocketErrorCode}).",
                    ex),
            };
        }

        return TvException.Unreachable(
            $"Could not open an SSAP connection to {_endpoint.Address}:{_endpoint.Port}.", ex);
    }

    public async Task<string> RegisterAsync(string? clientKey, CancellationToken cancellationToken)
    {
        var id = NextId("register");
        var payload = new Dictionary<string, object?>
        {
            ["forcePairing"] = false,
            ["pairingType"] = "PROMPT",
            ["manifest"] = SsapManifest.Build(),
        };

        if (!string.IsNullOrWhiteSpace(clientKey))
        {
            payload["client-key"] = clientKey;
        }

        var completion = Register(id);
        await SendRawAsync(
            JsonSerializer.Serialize(new { type = "register", id, payload }),
            cancellationToken).ConfigureAwait(false);

        JsonElement response;
        try
        {
            response = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TvException ex) when (ex.Code is TvErrorCode.TvError or TvErrorCode.PairingRequired)
        {
            // An SSAP error on the register frame is always a pairing problem,
            // but WHICH one matters to the caller:
            //
            //   no key supplied  -> there was no stale credential to reject, so
            //                       a human declined the on-screen prompt.
            //   key supplied     -> the stored key was refused; re-pair.
            //
            // Explicit reject/cancel wording wins over that inference. Note
            // "access denied" alone is NOT treated as a decline — it is the
            // ordinary wording for a refused stale key.
            throw IsExplicitDecline(ex.Message) || string.IsNullOrWhiteSpace(clientKey)
                ? TvException.PairingDenied()
                : TvException.PairingRequired();
        }
        finally
        {
            Unregister(id);
        }

        var accepted = JsonPayloadKey(response);
        if (!string.IsNullOrWhiteSpace(accepted))
        {
            return accepted!;
        }

        if (!string.IsNullOrWhiteSpace(clientKey))
        {
            return clientKey!;
        }

        throw TvException.PairingRequired();
    }

    internal static bool IsExplicitDecline(string? detail)
    {
        var text = detail ?? string.Empty;
        return text.Contains("reject", StringComparison.OrdinalIgnoreCase)
            || text.Contains("cancel", StringComparison.OrdinalIgnoreCase)
            || text.Contains("denied by", StringComparison.OrdinalIgnoreCase)
            || text.Contains("user denied", StringComparison.OrdinalIgnoreCase);
    }

    private static string? JsonPayloadKey(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in new[] { "client-key", "clientKey" })
        {
            if (payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    public async Task<JsonElement> RequestAsync(string uri, object? payload, CancellationToken cancellationToken)
    {
        EnsureUsable();

        var id = NextId("req");
        var frame = payload is null
            ? JsonSerializer.Serialize(new { type = "request", id, uri })
            : JsonSerializer.Serialize(new { type = "request", id, uri, payload });

        var completion = Register(id);
        try
        {
            await SendRawAsync(frame, cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Unregister(id);
        }
    }

    // ------------------------------------------------------- pointer socket

    public Task SendButtonAsync(string wireName, CancellationToken cancellationToken) =>
        SendPointerFrameAsync($"type:button\nname:{wireName}\n\n", cancellationToken);

    public Task SendPointerMoveAsync(int deltaX, int deltaY, bool drag, CancellationToken cancellationToken) =>
        SendPointerFrameAsync(
            $"type:move\ndx:{deltaX}\ndy:{deltaY}\ndown:{(drag ? 1 : 0)}\n\n", cancellationToken);

    public Task SendPointerClickAsync(CancellationToken cancellationToken) =>
        SendPointerFrameAsync("type:click\n\n", cancellationToken);

    public Task SendPointerScrollAsync(int deltaX, int deltaY, CancellationToken cancellationToken) =>
        SendPointerFrameAsync($"type:scroll\ndx:{deltaX}\ndy:{deltaY}\n\n", cancellationToken);

    private async Task SendPointerFrameAsync(string frame, CancellationToken cancellationToken)
    {
        var socket = await EnsurePointerSocketAsync(cancellationToken).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetBytes(frame);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<ClientWebSocket> EnsurePointerSocketAsync(CancellationToken cancellationToken)
    {
        if (_pointerSocket is { State: WebSocketState.Open })
        {
            return _pointerSocket;
        }

        DisposePointerSocket();

        var payload = await RequestAsync(SsapUriInternal.GetPointerInputSocket, null, cancellationToken)
            .ConfigureAwait(false);

        var path = payload.ValueKind == JsonValueKind.Object &&
                   payload.TryGetProperty("socketPath", out var socketPath) &&
                   socketPath.ValueKind == JsonValueKind.String
            ? socketPath.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(path) || !Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            throw TvException.Unsupported("pointer and remote-button input");
        }

        var socket = new ClientWebSocket();
        if (uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase))
        {
            socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        try
        {
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            socket.Dispose();
            throw TvException.Unreachable("Could not open the TV pointer input socket.", ex);
        }

        _pointerSocket = socket;
        return socket;
    }

    // ------------------------------------------------------------ read loop

    private async Task ReadLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult result;

                do
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        FaultAll(TvException.Unreachable("The TV closed the SSAP connection."));
                        return;
                    }

                    if (message.Length + result.Count > MaxMessageBytes)
                    {
                        FaultAll(TvException.Unreachable("The TV sent an oversized SSAP frame."));
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                Dispatch(Encoding.UTF8.GetString(message.ToArray()));
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            FaultAll(TvException.Unreachable("The SSAP connection failed while reading.", ex));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _faulted = true;
        }
    }

    private void Dispatch(string raw)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            _logger.LogDebug("Discarding malformed SSAP frame ({Length} bytes).", raw.Length);
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            var id = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : null;

            if (id is null)
            {
                return;
            }

            TaskCompletionSource<JsonElement>? completion;
            lock (_pendingLock)
            {
                _pending.TryGetValue(id, out completion);
            }

            if (completion is null)
            {
                return;
            }

            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;

            // Which frame this is decides how an authorization refusal is read.
            // The id prefix is the only thing that distinguishes them, and the
            // register handshake already relies on it below.
            var isRegistration = id.StartsWith("register", StringComparison.Ordinal);

            if (string.Equals(type, "error", StringComparison.Ordinal))
            {
                var detail = root.TryGetProperty("error", out var error) ? error.GetString() : "unknown SSAP error";
                completion.TrySetException(
                    isRegistration ? MapRegistrationError(detail) : MapRequestError(detail));
                return;
            }

            // The register handshake answers twice: an interim "response" while
            // the prompt is on screen, then "registered" with the key. Only the
            // second one completes the wait.
            if (string.Equals(type, "response", StringComparison.Ordinal) && isRegistration)
            {
                return;
            }

            var payload = root.TryGetProperty("payload", out var payloadElement)
                ? payloadElement.Clone()
                : default;

            if (payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty("returnValue", out var returnValue) &&
                returnValue.ValueKind == JsonValueKind.False)
            {
                var detail = payload.TryGetProperty("errorText", out var errorText)
                    ? errorText.GetString()
                    : "the TV rejected the request";
                completion.TrySetException(
                    isRegistration ? MapRegistrationError(detail) : MapRequestError(detail));
                return;
            }

            completion.TrySetResult(payload);
        }
    }

    /// <summary>
    /// Maps a failure on the REGISTRATION frame.
    ///
    /// Here — and only here — an authorization refusal really does mean the
    /// supplied key was not accepted, so it is <see cref="TvErrorCode.PairingRequired"/>.
    /// </summary>
    internal static TvException MapRegistrationError(string? detail)
    {
        var text = detail ?? "unknown SSAP error";

        if (IsAuthorizationRefusal(text) ||
            text.Contains("registration", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("not registered", StringComparison.OrdinalIgnoreCase))
        {
            return TvException.PairingRequired();
        }

        return MapCapabilityOrGeneric(text);
    }

    /// <summary>
    /// Maps a failure on an ordinary COMMAND frame, on a session that is already
    /// registered.
    ///
    /// The distinction from <see cref="MapRegistrationError"/> is the whole point.
    /// A registered session can be refused a single command because that
    /// capability was never granted to the pairing — the key is present, the
    /// session is live, and the very next command may succeed. Reporting that as
    /// PAIRING_REQUIRED is what produced "No valid client key" for tv_close_app
    /// immediately after another SSAP call succeeded on the same connection: it
    /// sends an operator to fix a pairing that was never broken, and buries the
    /// real cause.
    /// </summary>
    internal static TvException MapRequestError(string? detail)
    {
        var text = detail ?? "unknown SSAP error";

        if (IsAuthorizationRefusal(text))
        {
            return TvException.PermissionDenied(text);
        }

        return MapCapabilityOrGeneric(text);
    }

    /// <summary>
    /// Wording webOS uses to refuse for authorization reasons. 401 is included
    /// because current firmware answers "401 insufficient permissions" for a
    /// capability the manifest did not obtain.
    /// </summary>
    private static bool IsAuthorizationRefusal(string text) =>
        text.Contains("401", StringComparison.Ordinal) ||
        text.Contains("403", StringComparison.Ordinal) ||
        text.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("insufficient permission", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("forbidden", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Classification reads the RAW text — a control character must not be able to
    /// hide the word it sits inside — but every message built from it goes through
    /// <see cref="TvException.SanitizeDetail"/>, so nothing raw reaches a caller or
    /// a log line.
    /// </summary>
    private static TvException MapCapabilityOrGeneric(string text)
    {
        if (text.Contains("404", StringComparison.Ordinal) ||
            text.Contains("no such service", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("not exist", StringComparison.OrdinalIgnoreCase))
        {
            return TvException.Reported(TvErrorCode.TvUnsupportedCapability, text);
        }

        return TvException.Reported(TvErrorCode.TvError, text);
    }

    private void FaultAll(Exception exception)
    {
        _faulted = true;

        List<TaskCompletionSource<JsonElement>> waiting;
        lock (_pendingLock)
        {
            waiting = [.. _pending.Values];
            _pending.Clear();
        }

        foreach (var completion in waiting)
        {
            completion.TrySetException(exception);
        }
    }

    private void EnsureUsable()
    {
        if (!IsConnected)
        {
            throw TvException.Unreachable("The SSAP connection is not open.");
        }
    }

    private string NextId(string prefix) =>
        $"{prefix}_{Interlocked.Increment(ref _nextId)}";

    private TaskCompletionSource<JsonElement> Register(string id)
    {
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock)
        {
            _pending[id] = completion;
        }

        return completion;
    }

    private void Unregister(string id)
    {
        lock (_pendingLock)
        {
            _pending.Remove(id);
        }
    }

    private async Task SendRawAsync(string frame, CancellationToken cancellationToken)
    {
        var socket = _socket ?? throw TvException.Unreachable("The SSAP connection is not open.");
        var bytes = Encoding.UTF8.GetBytes(frame);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void DisposePointerSocket()
    {
        _pointerSocket?.Dispose();
        _pointerSocket = null;
    }

    public async ValueTask DisposeAsync()
    {
        _faulted = true;

        if (_readLoopCts is not null)
        {
            await _readLoopCts.CancelAsync().ConfigureAwait(false);
        }

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // Best effort — we are tearing the connection down anyway.
            }
        }

        FaultAll(TvException.Unreachable("The SSAP connection was closed."));

        DisposePointerSocket();
        _socket?.Dispose();
        _socket = null;
        _readLoopCts?.Dispose();
        _readLoopCts = null;
        _sendLock.Dispose();
    }
}

internal static class SsapUriInternal
{
    public const string GetPointerInputSocket = "ssap://com.webos.service.networkinput/getPointerInputSocket";
}

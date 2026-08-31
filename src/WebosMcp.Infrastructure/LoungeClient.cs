using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebosMcp.Application;
using WebosMcp.Domain;

namespace WebosMcp.Infrastructure;

/// <summary>
/// YouTube Lounge remote-control client.
///
/// This is the ONLY path that can load a specific video into an already-running
/// YouTube receiver, and the only one that reports back what is actually playing.
/// DIAL can do neither — a launch aimed at a running app is accepted and ignored,
/// and DIAL exposes no read-back — which is why every earlier DIAL-only attempt
/// reported success while the previous video kept playing.
///
/// It is a deliberate exception to this server's local-first design: Lounge is
/// reached over the internet at youtube.com, not on the LAN. That trade is
/// documented in the README rather than hidden.
///
/// The wire format is Google's undocumented browser-channel protocol: parameters
/// in the query string, commands as form fields, and events as a stream of
/// length-prefixed JSON chunks.
/// </summary>
public sealed class LoungeClient : ILoungeClient
{
    private const string TokenPath = "/api/lounge/pairing/get_lounge_token_batch";

    private readonly HttpClient _http;
    private readonly WebosMcpOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<LoungeClient> _logger;

    public LoungeClient(
        HttpClient http,
        IOptions<WebosMcpOptions> options,
        ILoggerFactory loggerFactory,
        ILogger<LoungeClient> logger)
    {
        _http = http;
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public async Task<ILoungeSession?> ConnectAsync(string screenId, CancellationToken cancellationToken)
    {
        var token = await GetLoungeTokenAsync(screenId, cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            _logger.LogWarning("No Lounge token was issued for the receiver; it cannot be controlled.");
            return null;
        }

        var session = new LoungeSession(
            _http,
            new Uri(_options.LoungeBaseUrl.TrimEnd('/')),
            token,
            _options.LoungeDeviceName,
            _loggerFactory.CreateLogger<LoungeSession>());

        if (!await session.BindAsync(cancellationToken).ConfigureAwait(false))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        return session;
    }

    /// <summary>
    /// Exchanges the DIAL screen id for a Lounge token. Null when YouTube issues
    /// none, which means this receiver cannot be remote-controlled.
    /// </summary>
    private async Task<string?> GetLoungeTokenAsync(string screenId, CancellationToken cancellationToken)
    {
        var url = new Uri(new Uri(_options.LoungeBaseUrl.TrimEnd('/') + "/"), TokenPath.TrimStart('/'));

        using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("screen_ids", screenId)]);

        try
        {
            using var response = await _http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new TvException(
                    TvErrorCode.TvError,
                    $"YouTube refused the Lounge token request with HTTP {(int)response.StatusCode}. " +
                    "The server needs outbound access to youtube.com for YouTube control.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseLoungeToken(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw TvException.Unreachable(
                $"Could not reach YouTube to obtain a Lounge token: {ex.Message}. " +
                "YouTube control requires outbound internet access, unlike the rest of this server.",
                ex);
        }
    }

    internal static string? ParseLoungeToken(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("screens", out var screens) ||
                screens.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var screen in screens.EnumerateArray())
            {
                if (screen.TryGetProperty("loungeToken", out var token) &&
                    token.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(token.GetString()))
                {
                    return token.GetString();
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// One bound remote session. Commands go out as form posts; the receiver's state
/// arrives on a long-poll stream of length-prefixed JSON chunks.
/// </summary>
internal sealed class LoungeSession : ILoungeSession
{
    private const string BindPath = "/api/lounge/bc/bind";

    private readonly HttpClient _http;
    private readonly Uri _baseUrl;
    private readonly string _loungeToken;
    private readonly string _deviceName;
    private readonly string _deviceId = Guid.NewGuid().ToString("N");
    private readonly ILogger<LoungeSession> _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private string? _sessionId;
    private string? _gSessionId;
    private int _requestId;
    private int _commandOffset;

    public LoungeSession(
        HttpClient http,
        Uri baseUrl,
        string loungeToken,
        string deviceName,
        ILogger<LoungeSession> logger)
    {
        _http = http;
        _baseUrl = baseUrl;
        _loungeToken = loungeToken;
        _deviceName = deviceName;
        _logger = logger;
    }

    /// <summary>Opens the channel and captures the session ids every later call needs.</summary>
    public async Task<bool> BindAsync(CancellationToken cancellationToken)
    {
        var url = BuildUrl(new Dictionary<string, string>
        {
            ["RID"] = Interlocked.Increment(ref _requestId).ToString(CultureInfo.InvariantCulture),
            ["CVER"] = "1",
        });

        using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("count", "0")]);

        try
        {
            using var response = await _http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Lounge bind was refused with HTTP {Status}.", (int)response.StatusCode);
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            foreach (var element in ParseChunks(body))
            {
                CaptureSessionIds(element);
            }

            return _sessionId is not null && _gSessionId is not null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw TvException.Unreachable($"Could not bind a YouTube Lounge session: {ex.Message}", ex);
        }
    }

    public async Task SendAsync(
        string command,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken)
    {
        // Serialised: two interleaved command posts would race on RID/ofs and the
        // receiver would reject or misorder them.
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var url = BuildUrl(new Dictionary<string, string>
            {
                ["RID"] = Interlocked.Increment(ref _requestId).ToString(CultureInfo.InvariantCulture),
                ["SID"] = _sessionId ?? string.Empty,
                ["gsessionid"] = _gSessionId ?? string.Empty,
                ["CVER"] = "1",
            });

            var fields = new List<KeyValuePair<string, string>>
            {
                new("count", "1"),
                new("ofs", (_commandOffset++).ToString(CultureInfo.InvariantCulture)),
                new("req0__sc", command),
            };

            foreach (var (key, value) in parameters ?? new Dictionary<string, string>())
            {
                fields.Add(new KeyValuePair<string, string>($"req0_{key}", value));
            }

            using var content = new FormUrlEncodedContent(fields);
            using var response = await _http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new TvException(
                    TvErrorCode.TvError,
                    $"The YouTube receiver refused the '{command}' command with HTTP {(int)response.StatusCode}.");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw TvException.Unreachable($"The '{command}' Lounge command could not be sent: {ex.Message}", ex);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async IAsyncEnumerable<LoungeReceiverState> ObserveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var lastEventId = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            string body;

            var url = BuildUrl(new Dictionary<string, string>
            {
                ["RID"] = "rpc",
                ["SID"] = _sessionId ?? string.Empty,
                ["gsessionid"] = _gSessionId ?? string.Empty,
                ["CI"] = "0",
                ["TYPE"] = "xmlhttp",
                ["AID"] = lastEventId.ToString(CultureInfo.InvariantCulture),
            });

            try
            {
                using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Lounge event stream returned HTTP {Status}; stopping observation.",
                        (int)response.StatusCode);
                    yield break;
                }

                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                yield break;
            }

            var sawAny = false;

            foreach (var element in ParseChunks(body))
            {
                lastEventId = Math.Max(lastEventId, EventId(element) ?? lastEventId);

                if (ParseReceiverState(element) is { } state)
                {
                    sawAny = true;
                    yield return state;
                }
            }

            if (!sawAny && body.Length == 0)
            {
                yield break;
            }
        }
    }

    private Uri BuildUrl(IReadOnlyDictionary<string, string> extra)
    {
        var query = new Dictionary<string, string>
        {
            ["device"] = "REMOTE_CONTROL",
            ["mdx-version"] = "3",
            ["ui"] = "false",
            ["v"] = "2",
            ["VER"] = "8",
            ["app"] = "webos-mcp",
            ["name"] = _deviceName,
            ["id"] = _deviceId,
            ["loungeIdToken"] = _loungeToken,
            ["t"] = "1",
        };

        foreach (var (key, value) in extra)
        {
            if (!string.IsNullOrEmpty(value))
            {
                query[key] = value;
            }
        }

        var encoded = string.Join(
            "&",
            query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        return new Uri(_baseUrl, $"{BindPath}?{encoded}");
    }

    private void CaptureSessionIds(JsonElement element)
    {
        // Session ids arrive as ["c", "<SID>", ...] and ["S", "<gsessionid>"].
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() < 2)
        {
            return;
        }

        var head = element[0];
        if (head.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var value = element[1];

        switch (head.GetString())
        {
            case "c" when value.ValueKind == JsonValueKind.String:
                _sessionId = value.GetString();
                break;
            case "S" when value.ValueKind == JsonValueKind.String:
                _gSessionId = value.GetString();
                break;
        }
    }

    /// <summary>
    /// Splits the browser-channel body into its inner event arrays. The wire form is
    /// a repeated "&lt;byte length&gt;\n&lt;json&gt;" and each JSON payload is
    /// [[id, [name, data...]], ...].
    /// </summary>
    internal static IReadOnlyList<JsonElement> ParseChunks(string body)
    {
        var events = new List<JsonElement>();
        var index = 0;

        while (index < body.Length)
        {
            var newline = body.IndexOf('\n', index);
            if (newline < 0)
            {
                break;
            }

            if (!int.TryParse(
                    body.AsSpan(index, newline - index).Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var length))
            {
                break;
            }

            var start = newline + 1;

            // Lengths are byte counts; clamp so a mismatch truncates rather than throws.
            var available = body.Length - start;
            var take = Math.Min(Math.Max(length, 0), available);
            var json = body.Substring(start, take);

            index = start + take;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var entry in document.RootElement.EnumerateArray())
                {
                    // Clone: the document is disposed at the end of this scope.
                    events.Add(entry.Clone());
                }
            }
        }

        return events;
    }

    internal static int? EventId(JsonElement entry) =>
        entry.ValueKind == JsonValueKind.Array &&
        entry.GetArrayLength() > 0 &&
        entry[0].ValueKind == JsonValueKind.Number &&
        entry[0].TryGetInt32(out var id)
            ? id
            : null;

    /// <summary>
    /// Reads a receiver state report out of one event entry. Recognises
    /// <c>nowPlaying</c> and <c>onStateChange</c>; everything else is ignored.
    /// </summary>
    internal static LoungeReceiverState? ParseReceiverState(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 2)
        {
            return null;
        }

        var payload = entry[1];
        if (payload.ValueKind != JsonValueKind.Array || payload.GetArrayLength() < 2)
        {
            return null;
        }

        var name = payload[0].ValueKind == JsonValueKind.String ? payload[0].GetString() : null;

        var data = payload[1];
        if (data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return name switch
        {
            "nowPlaying" or "onStateChange" => new LoungeReceiverState(
                Text(data, "videoId"),
                ParseState(Text(data, "state")),
                Number(data, "currentTime"),
                Number(data, "duration")),

            "onVolumeChanged" => new LoungeReceiverState(
                Volume: Number(data, "volume") is { } v ? (int)v : null),

            "autoplayModeChanged" or "onAutoplayModeChanged" => new LoungeReceiverState(
                AutoplayEnabled: Text(data, "autoplayMode") is { } mode
                    ? mode.Equals("ENABLED", StringComparison.OrdinalIgnoreCase)
                    : null),

            _ => null,
        };
    }

    internal static LoungePlayerState ParseState(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
        Enum.IsDefined(typeof(LoungePlayerState), value)
            ? (LoungePlayerState)value
            : LoungePlayerState.Unknown;

    private static string? Text(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? Number(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) => s,
            _ => null,
        };
    }

    public ValueTask DisposeAsync()
    {
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }
}

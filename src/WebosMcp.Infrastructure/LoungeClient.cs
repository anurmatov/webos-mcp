using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
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
            screenId,
            _options.LoungeDeviceName,
            _options.LoungeSubscribeTimeoutSeconds,
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

    /// <summary>
    /// The app identity the receiver expects from a remote. Not cosmetic — it is
    /// part of the handshake the receiver validates.
    /// </summary>
    private const string AppName = "youtube-desktop";

    private readonly HttpClient _http;
    private readonly Uri _baseUrl;
    private readonly string _loungeToken;
    private readonly string _screenId;
    private readonly string _deviceName;
    private readonly int _subscribeTimeoutSeconds;
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
        string screenId,
        string deviceName,
        int subscribeTimeoutSeconds,
        ILogger<LoungeSession> logger)
    {
        _http = http;
        _baseUrl = baseUrl;
        _loungeToken = loungeToken;
        _screenId = screenId;
        _deviceName = deviceName;
        _subscribeTimeoutSeconds = subscribeTimeoutSeconds;
        _logger = logger;
    }

    /// <summary>
    /// Opens the channel and captures the session ids every later call needs.
    ///
    /// The handshake shape is load-bearing and was established against real
    /// hardware: the receiver and device metadata go in the POST FORM BODY — with
    /// the receiver's own screen id as <c>id</c> — and only the channel parameters
    /// stay in the query. An earlier revision put the metadata in the query with a
    /// random client id and posted a bare <c>count=0</c>; the receiver refused it,
    /// so every YouTube tool failed at bind while a reference client controlling the
    /// same receiver connected immediately.
    /// </summary>
    public async Task<bool> BindAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildBindUrl())
        {
            Content = new FormUrlEncodedContent(BuildBindFields()),
        };

        // Also presented as a header, which is how reference clients authenticate.
        request.Headers.TryAddWithoutValidation("X-YouTube-LoungeId-Token", _loungeToken);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

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

            if (_sessionId is null || _gSessionId is null)
            {
                _logger.LogWarning("The Lounge bind response carried no session ids; the receiver cannot be controlled.");
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw TvException.Unreachable($"Could not bind a YouTube Lounge session: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Only the channel parameters. The token deliberately does NOT go here — it
    /// belongs in the form body, which also keeps it out of any URL that request
    /// logging might print.
    /// </summary>
    internal Uri BuildBindUrl() => new(
        _baseUrl,
        $"{BindPath}?RID={Uri.EscapeDataString(Interlocked.Increment(ref _requestId).ToString(CultureInfo.InvariantCulture))}" +
        "&VER=8&CVER=1&auth_failure_option=send_error");

    /// <summary>
    /// The bind form body. <c>id</c> is the RECEIVER's screen id, not a client-side
    /// identifier: that is what ties this remote to the running session.
    /// </summary>
    internal IReadOnlyList<KeyValuePair<string, string>> BuildBindFields() =>
    [
        new("app", AppName),
        new("mdx-version", "3"),
        new("name", _deviceName),
        new("id", _screenId),
        new("device", "REMOTE_CONTROL"),
        new("capabilities", "que,dsdtr,atp"),
        new("deviceContext", "user_agent=webos-mcp"),
        new("magnaKey", "cloudPairedDevice"),
        new("ui", "false"),
        new("theme", "cl"),
        new("loungeIdToken", _loungeToken),
    ];

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
            var url = BuildSessionUrl(new Dictionary<string, string>
            {
                ["RID"] = Interlocked.Increment(ref _requestId).ToString(CultureInfo.InvariantCulture),
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

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new FormUrlEncodedContent(fields),
            };
            request.Headers.TryAddWithoutValidation("X-YouTube-LoungeId-Token", _loungeToken);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Opens the event stream, STARTS CONSUMING IT, and returns only once the read
    /// loop is actually running against the receiver's stream.
    ///
    /// Readiness comes from the pump, not from the response headers. Headers coming
    /// back only says the request was accepted — nobody is reading yet, and a
    /// receiver that delivers an announcement to whoever is listening at that instant
    /// has nobody to deliver it to. The barrier is therefore the pump having ISSUED
    /// ITS FIRST READ on the stream: from that moment there is a reader outstanding
    /// and the announcement lands somewhere. Waiting for the first chunk instead
    /// would deadlock, because the stream is legitimately silent until the command
    /// this barrier exists to precede.
    ///
    /// Once running, the pump parses continuously into a buffer that
    /// <see cref="LoungeSubscription.ReadAsync"/> drains, so an event announced
    /// before the caller starts reading is held rather than lost.
    ///
    /// This exists because the opposite order silently loses events. The receiver
    /// announces a state change once, as it happens; a stream opened — or merely
    /// accepted — after the command can miss the announcement entirely, and the tool
    /// then reports a video that is visibly playing as never observed. A sleep would
    /// not fix it either: it asserts that enough time has passed rather than
    /// confirming anything is reading, so it is both slower and still a race.
    /// </summary>
    public async Task<ILoungeSubscription> SubscribeAsync(CancellationToken cancellationToken)
    {
        // Bounded on its own, separate from the verification budget: failing to get
        // the stream reading means nothing was sent to the receiver at all, which is
        // a different report from a command that went out unconfirmed.
        var seconds = Math.Max(1, _subscribeTimeoutSeconds);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(seconds));

        var poll = await OpenPollAsync(0, budget.Token).ConfigureAwait(false);

        if (poll is null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            throw new TvException(
                TvErrorCode.TvError,
                $"The YouTube receiver did not open its event stream within {seconds}s, so nothing it " +
                "was asked to do could be verified. No command was sent.");
        }

        var subscription = new LoungeSubscription(this, poll.Value.Response, poll.Value.Stream);

        try
        {
            // Headers are already back at this point. This is the part that matters:
            // it returns only once the pump has a read outstanding on the stream.
            if (await subscription.StartAsync(budget.Token).ConfigureAwait(false))
            {
                return subscription;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);

            throw new TvException(
                TvErrorCode.TvError,
                $"The YouTube receiver's event stream did not begin delivering within {seconds}s, so " +
                "nothing it was asked to do could be verified. No command was sent.");
        }
        catch
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        await subscription.DisposeAsync().ConfigureAwait(false);

        throw new TvException(
            TvErrorCode.TvError,
            "The YouTube receiver's event stream ended before anything could be read from it, so " +
            "nothing it was asked to do could be verified. No command was sent.");
    }

    /// <summary>
    /// Opens one long poll. Separated from the iterator because a catch block cannot
    /// contain a yield, and swallowing the failure inline would hide it.
    /// </summary>
    internal async Task<(HttpResponseMessage Response, Stream Stream)?> OpenPollAsync(
        int lastEventId,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildSubscriptionUrl(lastEventId));
            request.Headers.TryAddWithoutValidation("X-YouTube-LoungeId-Token", _loungeToken);

            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Lounge event stream returned HTTP {Status}; stopping observation.",
                    (int)response.StatusCode);

                response.Dispose();
                return null;
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return (response, stream);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            response?.Dispose();
            return null;
        }
    }

    /// <summary>Reads the next block, or -1 when the stream ends or faults.</summary>
    internal static async Task<int> ReadAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            return await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            return -1;
        }
    }

    /// <summary>
    /// The event-subscription query, as the reference client sends it. Deliberately
    /// fuller than the command query: the subscription re-presents the remote's
    /// identity, and the receiver would not feed a poll that omits it.
    ///
    /// The token is in the query here because that is the shape proven against
    /// hardware; the log filtering that keeps request URIs out of the log stream is
    /// what protects it. Do NOT reshape this to match the command query — they are
    /// different requests and only the command one is proven in that shorter form.
    /// </summary>
    internal Uri BuildSubscriptionUrl(int lastEventId)
    {
        var query = new Dictionary<string, string>
        {
            ["name"] = _deviceName,
            ["loungeIdToken"] = _loungeToken,
            ["device"] = "REMOTE_CONTROL",
            ["app"] = AppName,
            ["VER"] = "8",
            ["v"] = "2",
            ["RID"] = "rpc",
            ["SID"] = _sessionId ?? string.Empty,
            ["gsessionid"] = _gSessionId ?? string.Empty,
            ["CI"] = "0",
            ["TYPE"] = "xmlhttp",
            ["AID"] = lastEventId.ToString(CultureInfo.InvariantCulture),
        };

        var encoded = string.Join(
            "&",
            query.Where(kv => !string.IsNullOrEmpty(kv.Value))
                .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        return new Uri(_baseUrl, $"{BindPath}?{encoded}");
    }

    /// <summary>
    /// The query for a command post or an event poll: the app identity plus the
    /// receiver session fields, and nothing else.
    ///
    /// The device metadata that belongs in the BIND FORM BODY (id, mdx-version, ui,
    /// t) does not belong here, and the token does not either — it travels as a
    /// header on these paths, so no live credential is ever in a URL that request
    /// logging could print.
    /// </summary>
    private Uri BuildSessionUrl(IReadOnlyDictionary<string, string> extra)
    {
        var query = new Dictionary<string, string>
        {
            ["app"] = AppName,
            ["VER"] = "8",
            ["CVER"] = "1",
            ["auth_failure_option"] = "send_error",
            ["SID"] = _sessionId ?? string.Empty,
            ["gsessionid"] = _gSessionId ?? string.Empty,
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
            query.Where(kv => !string.IsNullOrEmpty(kv.Value))
                .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        return new Uri(_baseUrl, $"{BindPath}?{encoded}");
    }

    /// <summary>
    /// Reads the session ids out of a bind response entry.
    ///
    /// The framing is [id, [name, value, ...]] — the same envelope every event uses
    /// — so the name is in the INNER array. Reading the outer element's first slot
    /// finds the numeric event id and never matches, which is how this silently
    /// captured nothing.
    /// </summary>
    private void CaptureSessionIds(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() < 2)
        {
            return;
        }

        var payload = element[1];
        if (payload.ValueKind != JsonValueKind.Array || payload.GetArrayLength() < 2)
        {
            return;
        }

        if (payload[0].ValueKind != JsonValueKind.String)
        {
            return;
        }

        var value = payload[1];
        if (value.ValueKind != JsonValueKind.String)
        {
            return;
        }

        switch (payload[0].GetString())
        {
            case "c":
                _sessionId = value.GetString();
                break;
            case "S":
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
        // The length prefix is a BYTE count, not a character count. Decoding to a
        // string first and slicing by char index silently desynchronises the whole
        // stream the moment an event carries a non-ASCII character — a video title
        // in Cyrillic is enough — and every later chunk is then misread. So the
        // scan runs over UTF-8 bytes and only the payload is decoded.
        var bytes = Encoding.UTF8.GetBytes(body);
        var events = new List<JsonElement>();
        var index = 0;

        while (index < bytes.Length)
        {
            var newline = Array.IndexOf(bytes, (byte)'\n', index);
            if (newline < 0)
            {
                break;
            }

            var header = Encoding.ASCII.GetString(bytes, index, newline - index).Trim();

            if (!int.TryParse(header, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
            {
                break;
            }

            var start = newline + 1;

            // Clamp so a truncated trailing chunk stops the scan instead of throwing.
            var available = bytes.Length - start;
            var take = Math.Min(Math.Max(length, 0), available);
            var json = Encoding.UTF8.GetString(bytes, start, take);

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

/// <summary>
/// An event stream that was already open before the command it observes was sent.
///
/// The first poll is handed over ALREADY OPEN by <see cref="LoungeSession.SubscribeAsync"/>
/// — that is what makes the ordering guarantee real rather than nominal. Reading
/// lazily here instead would put the open back after the command and restore the
/// exact race this closes, because an iterator body does not begin executing until
/// its first enumeration.
/// </summary>
internal sealed class LoungeSubscription : ILoungeSubscription
{
    private readonly LoungeSession _session;

    /// <summary>
    /// Holds what the pump has parsed but the caller has not drained yet. Without
    /// it, an event announced between subscribing and the caller reading would be
    /// parsed and dropped — the same loss this class exists to prevent, moved one
    /// layer up.
    /// </summary>
    private readonly Channel<LoungeReceiverState> _states =
        Channel.CreateUnbounded<LoungeReceiverState>(new UnboundedChannelOptions { SingleWriter = true });

    /// <summary>
    /// True once the pump has a read outstanding; false if it ended without ever
    /// managing one. This is the readiness signal, and it comes from the pump itself.
    /// </summary>
    private readonly TaskCompletionSource<bool> _reading =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly CancellationTokenSource _stop = new();

    private HttpResponseMessage? _response;
    private Stream? _stream;
    private Task? _pump;
    private int _lastEventId;

    public LoungeSubscription(LoungeSession session, HttpResponseMessage response, Stream stream)
    {
        _session = session;
        _response = response;
        _stream = stream;
    }

    /// <summary>
    /// Starts the pump and waits for it to report that it is reading. False means it
    /// ended without ever issuing a read; cancellation of <paramref name="readinessBudget"/>
    /// means it did not get there in time. Either way the caller must not send a
    /// command — there would be nothing listening for the answer.
    ///
    /// The budget bounds only this wait. The pump itself runs for the life of the
    /// subscription and is stopped by <see cref="DisposeAsync"/>.
    /// </summary>
    internal async Task<bool> StartAsync(CancellationToken readinessBudget)
    {
        _pump = Task.Run(() => PumpAsync(_stop.Token), CancellationToken.None);

        return await _reading.Task.WaitAsync(readinessBudget).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the stream continuously from the moment the subscription starts, rather
    /// than when the caller gets round to enumerating. That is the whole point: the
    /// receiver announces once, to whoever is listening at that instant.
    /// </summary>
    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        // Taken, not copied: the established poll is consumed exactly once, and
        // disposal must not close a stream this loop still owns.
        var response = Interlocked.Exchange(ref _response, null);
        var stream = Interlocked.Exchange(ref _stream, null);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (response is null || stream is null)
                {
                    // Only reached after the established poll ends; the receiver
                    // closes long polls periodically and expects a fresh one.
                    var poll = await _session
                        .OpenPollAsync(_lastEventId, cancellationToken)
                        .ConfigureAwait(false);

                    if (poll is null)
                    {
                        return;
                    }

                    response = poll.Value.Response;
                    stream = poll.Value.Stream;
                }

                // Parsed INCREMENTALLY. This is a long poll: it stays open feeding
                // events as they happen, so waiting for the response to end means
                // waiting for the server to close it. That is why playback the
                // receiver really did start was never observed inside the
                // verification window — the video was playing and the report simply
                // had not been read yet.
                var chunks = new LoungeChunkStream();
                var buffer = new byte[8192];
                var readAny = false;

                while (true)
                {
                    // Deliberately not awaited on this line. Calling it issues the
                    // read, so the signal below is set while a reader is genuinely
                    // outstanding on the stream — which is what readiness means here.
                    // Awaiting first and signalling after would only ever report
                    // readiness once an event had ALREADY arrived, and the stream is
                    // silent until the command this barrier precedes.
                    var pending = LoungeSession.ReadAsync(stream, buffer, cancellationToken);

                    // A read that has ALREADY completed is not an outstanding reader:
                    // the stream either had something buffered or has ended, and in
                    // neither case is anything waiting on the receiver's next
                    // announcement. Signalling here would call a dead stream ready and
                    // let a command go out that nothing could verify. The next
                    // iteration's read is the one that will be pending — or the loop
                    // exits and readiness resolves false.
                    if (!pending.IsCompleted)
                    {
                        _reading.TrySetResult(true);
                    }

                    var read = await pending.ConfigureAwait(false);

                    if (read <= 0)
                    {
                        break;
                    }

                    readAny = true;

                    foreach (var state in chunks.Append(buffer.AsSpan(0, read)))
                    {
                        _states.Writer.TryWrite(state);
                    }
                }

                _lastEventId = Math.Max(_lastEventId, chunks.LastEventId);

                await ClosePollAsync(response, stream).ConfigureAwait(false);
                response = null;
                stream = null;

                if (!readAny)
                {
                    // The channel closed with nothing at all; re-polling would spin.
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal, or the caller giving up. Not a fault.
        }
        finally
        {
            await ClosePollAsync(response, stream).ConfigureAwait(false);

            // Unblocks a subscriber still waiting on readiness for a pump that never
            // got there, so it fails fast instead of sitting out the whole budget.
            _reading.TrySetResult(false);
            _states.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Drains what the pump has parsed. Everything announced since the subscription
    /// started is here, in order, whether or not the caller was reading at the time.
    /// </summary>
    public async IAsyncEnumerable<LoungeReceiverState> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var state in _states.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return state;
        }
    }

    private static async ValueTask ClosePollAsync(HttpResponseMessage? response, Stream? stream)
    {
        if (stream is not null)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        response?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);

        if (_pump is { } pump)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on disposal.
            }
        }

        // Non-null only when the pump never started — a failure between opening the
        // stream and getting it reading. Leaving it open would hold a poll against
        // the receiver for nothing.
        await ClosePollAsync(
            Interlocked.Exchange(ref _response, null),
            Interlocked.Exchange(ref _stream, null)).ConfigureAwait(false);

        _stop.Dispose();
    }
}

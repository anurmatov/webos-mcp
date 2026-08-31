using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebosMcp.Domain;

namespace WebosMcp.Application;

/// <param name="ExactVideoConfirmed">
/// Whether the requested video was observed to be the one playing. DIAL cannot
/// report the playing video, so this is false on every DIAL path — the caller is
/// told plainly rather than left to read success as a read-back confirmation.
/// </param>
/// <summary>
/// The outcome of one YouTube receiver command.
/// </summary>
/// <param name="Observed">
/// Whether the receiver actually confirmed the effect. False means the command was
/// ACCEPTED ONLY — the receiver announces no event for it — and must not be read as
/// the action having happened.
/// </param>
public sealed record YouTubeControlResult(
    string Command,
    bool Observed,
    string? ObservedVideoId = null,
    string? ObservedState = null,
    double? ObservedCurrentTime = null,
    int? ObservedVolume = null,
    bool? ObservedAutoplayEnabled = null,
    string Detail = "");

/// <param name="ObservedVideoId">The video id the receiver itself reported, when it reported one.</param>
/// <param name="ObservedState">The player state the receiver itself reported, when it reported one.</param>
public sealed record ContentActionResult(
    ActionPath Path,
    string Detail,
    string? AppId = null,
    bool ExactVideoConfirmed = false,
    string? ObservedVideoId = null,
    string? ObservedState = null);

/// <summary>
/// The shared capability layer. Both transports (stdio and Streamable HTTP)
/// serve exactly this — there is no transport-specific behaviour anywhere.
/// </summary>
public sealed class TvControlService
{
    private const string BrowserAppId = "com.webos.app.browser";

    /// <summary>The DIAL application name YouTube registers as.</summary>
    private const string DialYouTubeApp = "YouTube";

    /// <summary>Matched against the TV's reported foreground app id.</summary>
    private const string YouTubeAppIdFragment = "youtube";

    /// <summary>
    /// Apps whose on-screen keyboard silently swallows standard SSAP text
    /// entry. Physical testing confirmed YouTube does: insertText succeeds and
    /// nothing is typed. Reporting success for that is a lie, so text entry
    /// refuses outright when one of these is in the foreground.
    /// </summary>
    private static readonly string[] CustomKeyboardApps = ["youtube"];

    private readonly ITvSession _session;
    private readonly IDelayProvider _delay;
    private readonly IDialClient _dial;
    private readonly ILoungeClient _lounge;
    private readonly WebosMcpOptions _options;
    private readonly ILogger<TvControlService> _logger;

    public TvControlService(
        ITvSession session,
        IDelayProvider delay,
        IDialClient dial,
        ILoungeClient lounge,
        IOptions<WebosMcpOptions> options,
        ILogger<TvControlService> logger)
    {
        _session = session;
        _delay = delay;
        _dial = dial;
        _lounge = lounge;
        _options = options.Value;
        _logger = logger;
    }

    // ---------------------------------------------------------------- status

    public Task<PowerState> GetPowerStateAsync(CancellationToken ct) =>
        _session.ExecuteAsync("get_power_state", async (connection, token) =>
        {
            var payload = await connection.RequestAsync(SsapUri.GetPowerState, null, token).ConfigureAwait(false);
            return MapPowerState(JsonPayload.String(payload, "state"), JsonPayload.String(payload, "processing"));
        }, ct);

    internal static PowerState MapPowerState(string? state, string? processing)
    {
        var value = (state ?? string.Empty).Trim();

        // "Active Standby" must be checked before "Active" — a prefix match
        // would classify a standby TV as fully on.
        if (value.Contains("Active Standby", StringComparison.OrdinalIgnoreCase))
        {
            return PowerState.Standby;
        }

        if (value.Contains("Screen Off", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Screen Saver", StringComparison.OrdinalIgnoreCase))
        {
            return PowerState.ScreenOff;
        }

        if (value.Equals("Active", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(processing, "Screen Off", StringComparison.OrdinalIgnoreCase)
                ? PowerState.ScreenOff
                : PowerState.Active;
        }

        if (value.Contains("Suspend", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Standby", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Power Off", StringComparison.OrdinalIgnoreCase))
        {
            return PowerState.Standby;
        }

        return value.Length == 0 ? PowerState.Unknown : PowerState.Unknown;
    }

    public Task<SoftwareInfo> GetSoftwareInfoAsync(CancellationToken ct) =>
        _session.ExecuteAsync("get_software_info", async (connection, token) =>
        {
            var payload = await connection.RequestAsync(SsapUri.GetSoftwareInfo, null, token).ConfigureAwait(false);
            return new SoftwareInfo(
                JsonPayload.String(payload, "model_name", "modelName"),
                JsonPayload.String(payload, "major_ver", "majorVer") is { } major &&
                JsonPayload.String(payload, "minor_ver", "minorVer") is { } minor
                    ? $"{major}.{minor}"
                    : JsonPayload.String(payload, "firmwareVersion"),
                JsonPayload.String(payload, "major_ver", "majorVer"),
                JsonPayload.String(payload, "minor_ver", "minorVer"),
                JsonPayload.String(payload, "product_name", "productName"));
        }, ct);

    public Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct) =>
        _session.ExecuteAsync("get_system_info", async (connection, token) =>
        {
            var payload = await connection.RequestAsync(SsapUri.GetSystemInfo, null, token).ConfigureAwait(false);
            var features = JsonPayload.Object(payload, "features");
            return new SystemInfo(
                JsonPayload.String(payload, "modelName"),
                JsonPayload.String(payload, "receiverType"),
                features?.ToString());
        }, ct);

    public Task<ForegroundApp> GetForegroundAppAsync(CancellationToken ct) =>
        _session.ExecuteAsync("get_foreground_app", async (connection, token) =>
        {
            var payload = await connection.RequestAsync(SsapUri.GetForegroundApp, null, token).ConfigureAwait(false);
            return new ForegroundApp(
                JsonPayload.String(payload, "appId"),
                JsonPayload.String(payload, "windowId"),
                JsonPayload.String(payload, "processId"));
        }, ct);

    public Task<IReadOnlyList<AppInfo>> ListAppsAsync(CancellationToken ct) =>
        _session.ExecuteAsync<IReadOnlyList<AppInfo>>("list_apps", async (connection, token) =>
        {
            var payload = await connection.RequestAsync(SsapUri.ListLaunchPoints, null, token).ConfigureAwait(false);
            var apps = new List<AppInfo>();
            foreach (var item in JsonPayload.Array(payload, "launchPoints"))
            {
                var id = JsonPayload.String(item, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                apps.Add(new AppInfo(
                    id,
                    JsonPayload.String(item, "title") ?? id,
                    JsonPayload.String(item, "version"),
                    JsonPayload.Bool(item, "systemApp") ?? false));
            }

            return apps;
        }, ct);

    // ----------------------------------------------------------------- audio

    public Task<VolumeState> GetVolumeAsync(CancellationToken ct) =>
        _session.ExecuteAsync("get_volume", async (connection, token) =>
        {
            var payload = await connection.RequestAsync(SsapUri.GetVolume, null, token).ConfigureAwait(false);
            return ReadVolumeState(payload);
        }, ct);

    internal static VolumeState ReadVolumeState(JsonElement payload)
    {
        // Newer firmware nests these under volumeStatus; older firmware is flat.
        var status = JsonPayload.Object(payload, "volumeStatus") ?? payload;

        return new VolumeState(
            JsonPayload.Int(status, "volume") ?? JsonPayload.Int(payload, "volume") ?? 0,
            JsonPayload.Bool(status, "muteStatus", "mute") ?? JsonPayload.Bool(payload, "mute") ?? false,
            JsonPayload.String(status, "soundOutput") ?? JsonPayload.String(payload, "soundOutput"),
            JsonPayload.Int(status, "volumeMin", "minVolume"),
            JsonPayload.Int(status, "volumeMax", "maxVolume"));
    }

    public Task SetVolumeAsync(int volume, CancellationToken ct)
    {
        var validated = InputValidation.ValidateVolume(volume);
        return _session.ExecuteAsync("set_volume", (connection, token) =>
            connection.RequestAsync(SsapUri.SetVolume, new { volume = validated }, token), ct);
    }

    public Task SetMuteAsync(bool muted, CancellationToken ct) =>
        _session.ExecuteAsync("set_mute", (connection, token) =>
            connection.RequestAsync(SsapUri.SetMute, new { mute = muted }, token), ct);

    public Task<IReadOnlyList<string>> ListSoundOutputsAsync(CancellationToken ct) =>
        _session.ExecuteAsync<IReadOnlyList<string>>("list_sound_outputs", async (connection, token) =>
        {
            var payload = await connection.RequestAsync(SsapUri.GetSoundOutput, null, token).ConfigureAwait(false);
            var outputs = new List<string>();
            foreach (var item in JsonPayload.Array(payload, "soundOutputList", "soundOutputs"))
            {
                var value = item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : JsonPayload.String(item, "soundOutput", "id");
                if (!string.IsNullOrWhiteSpace(value))
                {
                    outputs.Add(value!);
                }
            }

            if (outputs.Count == 0 && JsonPayload.String(payload, "soundOutput") is { } current)
            {
                outputs.Add(current);
            }

            return outputs;
        }, ct);

    /// <summary>
    /// Validates the requested output against what the TV actually reports
    /// rather than trusting the caller's string.
    /// </summary>
    public async Task SetSoundOutputAsync(string output, CancellationToken ct)
    {
        var requested = (output ?? string.Empty).Trim();
        if (requested.Length == 0)
        {
            throw TvException.Invalid("A sound output is required.");
        }

        var available = await ListSoundOutputsAsync(ct).ConfigureAwait(false);
        var match = available.FirstOrDefault(o => o.Equals(requested, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw available.Count == 0
                ? TvException.Unsupported("sound output selection")
                : TvException.Invalid(
                    $"Sound output '{requested}' is not offered by this TV. Available: {string.Join(", ", available)}.");
        }

        await _session.ExecuteAsync("set_sound_output", (connection, token) =>
            connection.RequestAsync(SsapUri.ChangeSoundOutput, new { output = match }, token), ct)
            .ConfigureAwait(false);
    }

    public Task MediaControlAsync(MediaCommand command, CancellationToken ct)
    {
        var uri = command switch
        {
            MediaCommand.Play => SsapUri.MediaPlay,
            MediaCommand.Pause => SsapUri.MediaPause,
            MediaCommand.Stop => SsapUri.MediaStop,
            MediaCommand.Rewind => SsapUri.MediaRewind,
            MediaCommand.FastForward => SsapUri.MediaFastForward,
            _ => throw TvException.Invalid($"Unknown media command '{command}'."),
        };

        return _session.ExecuteAsync($"media_{command}", (connection, token) =>
            connection.RequestAsync(uri, null, token), ct);
    }

    // ------------------------------------------------------------ apps/media

    public Task LaunchAppAsync(string appId, CancellationToken ct)
    {
        var validated = InputValidation.ValidateAppId(appId);
        return _session.ExecuteAsync("launch_app", (connection, token) =>
            connection.RequestAsync(SsapUri.LaunchApp, new { id = validated }, token), ct);
    }

    public Task CloseAppAsync(string appId, CancellationToken ct)
    {
        var validated = InputValidation.ValidateAppId(appId);
        return _session.ExecuteAsync("close_app", (connection, token) =>
            connection.RequestAsync(SsapUri.CloseApp, new { id = validated }, token), ct);
    }

    public async Task<ContentActionResult> OpenUrlAsync(string url, CancellationToken ct)
    {
        var uri = InputValidation.ValidateHttpsUrl(url);

        return await _session.ExecuteAsync("open_url", async (connection, token) =>
        {
            await connection.RequestAsync(SsapUri.OpenBrowser, new { target = uri.AbsoluteUri }, token)
                .ConfigureAwait(false);
            return new ContentActionResult(
                ActionPath.DeepLink,
                $"Opened {uri.AbsoluteUri} in the webOS browser via system.launcher/open.",
                BrowserAppId);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// YouTube search has NO verifiable mechanism on this TV.
    ///
    /// The previous implementation launched YouTube and typed the query with
    /// ssap://com.webos.service.ime/insertText. Physical testing showed that
    /// YouTube's custom on-screen keyboard silently ignores that input: the
    /// call succeeds, nothing is typed, and the tool reported success while
    /// the TV sat on the home screen. The bounded fallback has therefore been
    /// removed rather than repaired — it could not be made honest.
    ///
    /// DIAL carries a video id but has no documented search parameter, so
    /// there is nothing to verify a search against either. Until a genuinely
    /// verifiable path exists this reports unsupported, which is the truthful
    /// answer.
    /// </summary>
    public Task<ContentActionResult> SearchYouTubeAsync(string query, CancellationToken ct)
    {
        // Still validated, so a malformed query is rejected as bad input
        // rather than masked by the unsupported response.
        InputValidation.ValidateSearchQuery(query);

        throw new TvException(
            TvErrorCode.TvUnsupportedCapability,
            "Searching YouTube on the TV is not supported. YouTube's custom on-screen keyboard ignores " +
            "standard text entry, and DIAL exposes no search parameter, so there is no way to confirm a " +
            "search actually ran. Use tv_youtube_play with a video id or URL instead.");
    }

    /// <summary>
    /// Plays a YouTube video via DIAL, and reports success ONLY after
    /// observing YouTube actually reach the foreground.
    ///
    /// SSAP's launcher is deliberately not used here: physical testing showed
    /// it accepting the request and returning success while the TV stayed on
    /// the home screen. A launch that is merely accepted is not playback.
    /// </summary>
    public async Task<ContentActionResult> PlayYouTubeAsync(string videoOrUrl, CancellationToken ct)
    {
        var videoId = InputValidation.ValidateYouTubeVideoId(videoOrUrl);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        var applicationUrl = await _dial.ResolveApplicationUrlAsync(ct).ConfigureAwait(false);
        if (applicationUrl is null)
        {
            throw new TvException(
                TvErrorCode.TvUnsupportedCapability,
                "This TV exposes no DIAL endpoint, so a YouTube launch cannot be performed or confirmed. " +
                "DIAL is normally advertised over SSDP on the local segment; check the TV is on the same " +
                "segment and that network control is enabled.");
        }

        var status = await _dial.GetAppStatusAsync(applicationUrl, DialYouTubeApp, ct).ConfigureAwait(false);
        if (status is null || !status.Installed)
        {
            throw TvException.Unsupported("the YouTube DIAL application (it is not installed on this TV)");
        }

        // DIAL is used ONLY to find the receiver. It cannot select a video in a
        // running YouTube session (the launch is accepted and ignored) and it cannot
        // report which video is playing, which is how earlier revisions reported
        // success over the wrong video. Lounge does both.
        //
        // Cold-restarting YouTube to force the video is deliberately NOT done: on the
        // physical TV that lands on the account/profile picker, which is worse than
        // the bug it would work around.
        if (!status.IsRunning)
        {
            // Nothing to control yet, so start the receiver. This is a launch of a
            // stopped app, not a restart to select a video.
            await _dial.LaunchAppAsync(applicationUrl, DialYouTubeApp, string.Empty, ct).ConfigureAwait(false);
            status = await WaitForReceiverAsync(applicationUrl, ct).ConfigureAwait(false);
        }

        if (status?.ScreenId is not { Length: > 0 } screenId)
        {
            throw new TvException(
                TvErrorCode.TvUnsupportedCapability,
                "The YouTube receiver on this TV advertises no screen id, so it cannot be remote-controlled " +
                "and the requested video cannot be loaded or verified. Reporting unsupported rather than " +
                "launching something unverifiable.");
        }

        await using var session = await ConnectLoungeAsync(screenId, ct).ConfigureAwait(false);

        // The event stream is opened AND ACTIVELY READ before the command, and this
        // returning is the barrier — a read is outstanding on it. The receiver
        // announces the video change once, as it happens, to whoever is listening at
        // that instant, so a stream opened afterwards — or merely accepted, headers
        // back, with nothing reading it — can miss the announcement outright. That is
        // how a video physically playing on the TV was reported as never observed. No
        // sleep is used or wanted here; a sleep asserts elapsed time instead of
        // confirming anything is reading, which leaves the same race, slower.
        await using var subscription = await session.SubscribeAsync(ct).ConfigureAwait(false);

        await session.SendAsync(
            "setPlaylist",
            new Dictionary<string, string>
            {
                ["videoId"] = videoId,
                ["currentIndex"] = "0",
                ["currentTime"] = "0",
                ["audioOnly"] = "false",
                ["listId"] = string.Empty,
            },
            ct).ConfigureAwait(false);

        // Acceptance is still not playback. Wait for the RECEIVER to report this
        // video id in a playing state, on the stream that was already open when the
        // command went out — the first read-back this tool has ever had.
        var observed = await ObserveAsync(
            subscription,
            state => string.Equals(state.VideoId, videoId, StringComparison.Ordinal)
                     && state.State is LoungePlayerState.Playing,
            ct).ConfigureAwait(false);

        if (observed is null)
        {
            throw new TvException(
                TvErrorCode.TvError,
                $"The receiver accepted the request for video '{videoId}' but never reported it playing " +
                $"within {_options.LoungeVerifyTimeoutSeconds}s. Reporting failure rather than an " +
                "unverified success.");
        }

        return new ContentActionResult(
            ActionPath.Lounge,
            $"Loaded video '{videoId}' into the running YouTube receiver and observed it reported back as " +
            $"playing after {Elapsed(started):0.0}s.",
            AppId: null,
            ExactVideoConfirmed: true,
            ObservedVideoId: observed.VideoId,
            ObservedState: observed.State.ToString());
    }

    /// <summary>
    /// Opens a Lounge session, or reports the receiver as uncontrollable. Every
    /// YouTube control tool goes through here.
    /// </summary>
    private async Task<ILoungeSession> ConnectLoungeAsync(string screenId, CancellationToken ct)
    {
        var session = await _lounge.ConnectAsync(screenId, ct).ConfigureAwait(false);

        return session ?? throw new TvException(
            TvErrorCode.TvUnsupportedCapability,
            "The YouTube receiver did not accept a remote-control session, so the requested video cannot " +
            "be loaded or verified on this TV.");
    }

    /// <summary>
    /// Consumes receiver state reports until one satisfies <paramref name="predicate"/>
    /// or the budget expires. Null means nothing matching was ever observed — which
    /// callers report as failure, never as an assumed success.
    ///
    /// Takes an ALREADY ESTABLISHED subscription rather than a session on purpose:
    /// the type makes it impossible to reach observation without having opened the
    /// stream first, so the command-before-subscribe ordering cannot come back.
    /// </summary>
    private async Task<LoungeReceiverState?> ObserveAsync(
        ILoungeSubscription subscription,
        Func<LoungeReceiverState, bool> predicate,
        CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.LoungeVerifyTimeoutSeconds)));

        try
        {
            await foreach (var state in subscription.ReadAsync(budget.Token).ConfigureAwait(false))
            {
                if (predicate(state))
                {
                    return state;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Budget expired without a matching report.
        }

        return null;
    }

    // ------------------------------------------------- youtube receiver control
    //
    // Every command below is sent over Lounge and then judged on what the RECEIVER
    // reports back. Where the receiver announces no confirming event, the result
    // says the command was accepted and NOT observed, rather than dressing
    // acceptance up as success — the distinction this project keeps getting wrong
    // when it is left implicit.

    public Task<YouTubeControlResult> YouTubePauseAsync(CancellationToken ct) =>
        ControlAsync("pause", null, s => s.State is LoungePlayerState.Paused, ct);

    public Task<YouTubeControlResult> YouTubeResumeAsync(CancellationToken ct) =>
        ControlAsync("play", null, s => s.State is LoungePlayerState.Playing, ct);

    public Task<YouTubeControlResult> YouTubeNextAsync(CancellationToken ct) =>
        ControlAsync("next", null, s => s.VideoId is { Length: > 0 }, ct);

    public Task<YouTubeControlResult> YouTubePreviousAsync(CancellationToken ct) =>
        ControlAsync("previous", null, s => s.VideoId is { Length: > 0 }, ct);

    public Task<YouTubeControlResult> YouTubeSeekAsync(double seconds, CancellationToken ct)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
        {
            throw TvException.Invalid($"Seek position must be a non-negative number of seconds, not '{seconds}'.");
        }

        return ControlAsync(
            "seekTo",
            new Dictionary<string, string> { ["newTime"] = seconds.ToString("0.###", CultureInfo.InvariantCulture) },
            s => s.CurrentTime is not null,
            ct);
    }

    public Task<YouTubeControlResult> YouTubeSetVolumeAsync(int volume, CancellationToken ct)
    {
        if (volume is < 0 or > 100)
        {
            throw TvException.Invalid($"Receiver volume must be between 0 and 100, not {volume}.");
        }

        return ControlAsync(
            "setVolume",
            new Dictionary<string, string> { ["volume"] = volume.ToString(CultureInfo.InvariantCulture) },
            s => s.Volume is not null,
            ct);
    }

    public Task<YouTubeControlResult> YouTubeSetAutoplayAsync(bool enabled, CancellationToken ct) =>
        ControlAsync(
            "setAutoplayMode",
            new Dictionary<string, string> { ["autoplayMode"] = enabled ? "ENABLED" : "DISABLED" },
            s => s.AutoplayEnabled == enabled,
            ct);

    public Task<YouTubeControlResult> YouTubeQueueAddAsync(string videoOrUrl, CancellationToken ct)
    {
        // setPlaylist with comma-separated ids did not build a reliable queue on the
        // physical receiver; sequential addVideo did. Do not "optimise" this into a
        // batched setPlaylist.
        var videoId = InputValidation.ValidateYouTubeVideoId(videoOrUrl);

        return ControlAsync(
            "addVideo",
            new Dictionary<string, string> { ["videoId"] = videoId },
            observed: null,
            ct);
    }

    /// <summary>
    /// Playback speed is sent and accepted, but this receiver announces no speed
    /// event, so there is nothing to observe. Reported as accepted-not-observed
    /// rather than as success.
    /// </summary>
    public Task<YouTubeControlResult> YouTubeSetPlaybackSpeedAsync(double speed, CancellationToken ct)
    {
        if (speed is < 0.25 or > 2.0)
        {
            throw TvException.Invalid($"Playback speed must be between 0.25 and 2.0, not {speed}.");
        }

        return ControlAsync(
            "setPlaybackSpeed",
            new Dictionary<string, string>
            {
                ["playbackSpeed"] = speed.ToString("0.###", CultureInfo.InvariantCulture),
            },
            observed: null,
            ct);
    }

    /// <summary>Reads the receiver's own current report. Pure observation, no command.</summary>
    public async Task<YouTubeControlResult> YouTubeNowPlayingAsync(CancellationToken ct)
    {
        await using var session = await OpenReceiverAsync(ct).ConfigureAwait(false);

        // Same ordering rule as every other observed path: the receiver answers
        // getNowPlaying on the event stream, so the stream is open before it is asked.
        await using var subscription = await session.SubscribeAsync(ct).ConfigureAwait(false);

        await session.SendAsync("getNowPlaying", null, ct).ConfigureAwait(false);

        var state = await ObserveAsync(subscription, s => s.VideoId is { Length: > 0 }, ct).ConfigureAwait(false);

        return state is null
            ? new YouTubeControlResult(
                "getNowPlaying",
                Observed: false,
                Detail: "The receiver reported nothing within the wait window, so there is no state to report.")
            : Describe("getNowPlaying", state, observed: true);
    }

    /// <summary>
    /// Sends one Lounge command and judges it on the receiver's own report.
    /// A null <paramref name="observed"/> means the receiver announces no confirming
    /// event for this command, so the result is explicitly accepted-not-observed.
    /// </summary>
    private async Task<YouTubeControlResult> ControlAsync(
        string command,
        IReadOnlyDictionary<string, string>? parameters,
        Func<LoungeReceiverState, bool>? observed,
        CancellationToken ct)
    {
        await using var session = await OpenReceiverAsync(ct).ConfigureAwait(false);

        if (observed is null)
        {
            // Nothing to observe, so no stream is opened — this command is reported
            // as accepted-not-observed either way.
            await session.SendAsync(command, parameters, ct).ConfigureAwait(false);

            return new YouTubeControlResult(
                command,
                Observed: false,
                Detail: $"The receiver accepted '{command}'. It announces no event confirming this command, " +
                        "so the effect was NOT observed and is not being reported as verified.");
        }

        // Established before the command, for the reason spelled out in PlayYouTubeAsync:
        // the confirming event is announced once and a stream opened afterwards can miss it.
        await using var subscription = await session.SubscribeAsync(ct).ConfigureAwait(false);

        await session.SendAsync(command, parameters, ct).ConfigureAwait(false);

        var state = await ObserveAsync(subscription, observed, ct).ConfigureAwait(false);

        if (state is null)
        {
            throw new TvException(
                TvErrorCode.TvError,
                $"The receiver accepted '{command}' but never reported the expected change within " +
                $"{_options.LoungeVerifyTimeoutSeconds}s. Reporting failure rather than an unverified success.");
        }

        return Describe(command, state, observed: true);
    }

    private static YouTubeControlResult Describe(string command, LoungeReceiverState state, bool observed) =>
        new(command,
            observed,
            state.VideoId,
            state.State == LoungePlayerState.Unknown ? null : state.State.ToString(),
            state.CurrentTime,
            state.Volume,
            state.AutoplayEnabled,
            $"The receiver confirmed '{command}' by reporting back its own state.");

    /// <summary>
    /// Opens a Lounge session against the RUNNING receiver. Deliberately does not
    /// launch or restart YouTube: these are control commands for a session that is
    /// already playing, and restarting would drop the TV on the profile picker.
    /// </summary>
    private async Task<ILoungeSession> OpenReceiverAsync(CancellationToken ct)
    {
        var applicationUrl = await _dial.ResolveApplicationUrlAsync(ct).ConfigureAwait(false)
            ?? throw new TvException(
                TvErrorCode.TvUnsupportedCapability,
                "This TV exposes no DIAL endpoint, so the YouTube receiver cannot be found or controlled.");

        var status = await _dial.GetAppStatusAsync(applicationUrl, DialYouTubeApp, ct).ConfigureAwait(false);

        if (status is null || !status.Installed)
        {
            throw TvException.Unsupported("the YouTube DIAL application (it is not installed on this TV)");
        }

        if (!status.IsRunning)
        {
            throw new TvException(
                TvErrorCode.TvUnsupportedCapability,
                "YouTube is not running on the TV, so there is no receiver session to control. " +
                "Start playback with tv_youtube_play first.");
        }

        if (status.ScreenId is not { Length: > 0 } screenId)
        {
            throw new TvException(
                TvErrorCode.TvUnsupportedCapability,
                "The YouTube receiver advertises no screen id, so it cannot be remote-controlled.");
        }

        return await ConnectLoungeAsync(screenId, ct).ConfigureAwait(false);
    }

    /// <summary>Polls DIAL until the receiver reports itself running, so a screen id exists.</summary>
    private async Task<DialAppStatus?> WaitForReceiverAsync(Uri applicationUrl, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.LaunchPollIntervalSeconds));
        var maxAttempts = (int)Math.Ceiling(
            Math.Max(1, _options.LaunchVerifyTimeoutSeconds) / interval.TotalSeconds);

        DialAppStatus? status = null;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            status = await _dial.GetAppStatusAsync(applicationUrl, DialYouTubeApp, ct).ConfigureAwait(false);

            if (status is { IsRunning: true, ScreenId: { Length: > 0 } })
            {
                return status;
            }

            await _delay.DelayAsync(interval, ct).ConfigureAwait(false);
        }

        return status;
    }

    /// <summary>
    /// Polls the TV's own foreground-app report until it names the expected
    /// app or the budget expires. Bounded by an attempt count as well as
    /// wall-clock so it is deterministic under a fake delay provider.
    /// </summary>
    private async Task<LaunchEvidence> ConfirmForegroundAsync(
        string expectedAppFragment,
        bool dialEndpointFound,
        bool dialLaunchAccepted,
        long started,
        CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.LaunchPollIntervalSeconds));
        var budget = TimeSpan.FromSeconds(Math.Max(1, _options.LaunchVerifyTimeoutSeconds));
        var maxAttempts = (int)Math.Ceiling(budget.TotalSeconds / interval.TotalSeconds);

        string? appId = null;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                appId = (await GetForegroundAppAsync(ct).ConfigureAwait(false)).AppId;
            }
            catch (TvException)
            {
                // The TV can briefly refuse while an app is starting. Keep
                // polling; the budget still bounds us.
                appId = null;
            }

            if (appId is not null &&
                appId.Contains(expectedAppFragment, StringComparison.OrdinalIgnoreCase))
            {
                return new LaunchEvidence(
                    dialEndpointFound, dialLaunchAccepted, true, appId, Elapsed(started));
            }

            await _delay.DelayAsync(interval, ct).ConfigureAwait(false);
        }

        return new LaunchEvidence(
            dialEndpointFound, dialLaunchAccepted, false, appId, Elapsed(started));
    }

    private static double Elapsed(long started) =>
        System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;

    // ------------------------------------------------------------ navigation

    public Task SendButtonAsync(RemoteButton button, int repeat, CancellationToken ct)
    {
        var count = InputValidation.ValidateRepeat(repeat);
        var wire = button.ToWireName();

        // The whole repeat sequence runs inside one ExecuteAsync so a second
        // caller cannot interleave its own presses between ours.
        return _session.ExecuteAsync("send_button", async (connection, token) =>
        {
            for (var i = 0; i < count; i++)
            {
                await connection.SendButtonAsync(wire, token).ConfigureAwait(false);
                if (i < count - 1)
                {
                    await StepDelayAsync(token).ConfigureAwait(false);
                }
            }
        }, ct);
    }

    public async Task TypeTextAsync(string text, bool replace, bool submit, CancellationToken ct)
    {
        var validated = InputValidation.ValidateText(text);

        // Refuse rather than no-op. The SSAP call would succeed and type
        // nothing, which is exactly the false success this tool must not
        // produce.
        var foreground = await GetForegroundAppAsync(ct).ConfigureAwait(false);
        if (foreground.AppId is { } appId &&
            CustomKeyboardApps.Any(a => appId.Contains(a, StringComparison.OrdinalIgnoreCase)))
        {
            throw new TvException(
                TvErrorCode.TvUnsupportedCapability,
                $"'{appId}' uses a custom on-screen keyboard that ignores standard text entry, so typing " +
                "into it would silently do nothing. Use the remote-control buttons (tv_send_button) to drive " +
                "its keyboard, or tv_youtube_play to open a video directly.");
        }

        await _session.ExecuteAsync("type_text", async (connection, token) =>
        {
            await connection.RequestAsync(
                SsapUri.InsertText,
                new { text = validated, replace },
                token).ConfigureAwait(false);

            if (submit)
            {
                await StepDelayAsync(token).ConfigureAwait(false);
                await connection.RequestAsync(SsapUri.SendEnterKey, null, token).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
    }

    public Task DeleteCharactersAsync(int count, CancellationToken ct)
    {
        var validated = InputValidation.ValidateRepeat(count);
        return _session.ExecuteAsync("delete_characters", (connection, token) =>
            connection.RequestAsync(SsapUri.DeleteCharacters, new { count = validated }, token), ct);
    }

    public Task SendEnterAsync(CancellationToken ct) =>
        _session.ExecuteAsync("send_enter", (connection, token) =>
            connection.RequestAsync(SsapUri.SendEnterKey, null, token), ct);

    public Task PointerMoveAsync(int deltaX, int deltaY, bool drag, CancellationToken ct)
    {
        var x = InputValidation.ValidatePointerDelta(deltaX, "x");
        var y = InputValidation.ValidatePointerDelta(deltaY, "y");
        return _session.ExecuteAsync("pointer_move", (connection, token) =>
            connection.SendPointerMoveAsync(x, y, drag, token), ct);
    }

    public Task PointerClickAsync(CancellationToken ct) =>
        _session.ExecuteAsync("pointer_click", (connection, token) =>
            connection.SendPointerClickAsync(token), ct);

    public Task PointerScrollAsync(int deltaX, int deltaY, CancellationToken ct)
    {
        var x = InputValidation.ValidatePointerDelta(deltaX, "x");
        var y = InputValidation.ValidatePointerDelta(deltaY, "y");
        return _session.ExecuteAsync("pointer_scroll", (connection, token) =>
            connection.SendPointerScrollAsync(x, y, token), ct);
    }

    // -------------------------------------------------------------- tv/input

    public Task<IReadOnlyList<ExternalInput>> ListInputsAsync(CancellationToken ct) =>
        _session.ExecuteAsync<IReadOnlyList<ExternalInput>>("list_inputs", async (connection, token) =>
        {
            var payload = await connection.RequestAsync(SsapUri.GetExternalInputList, null, token)
                .ConfigureAwait(false);

            var inputs = new List<ExternalInput>();
            foreach (var item in JsonPayload.Array(payload, "devices", "deviceList"))
            {
                var id = JsonPayload.String(item, "id", "appId");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                inputs.Add(new ExternalInput(
                    id,
                    JsonPayload.String(item, "label", "name") ?? id,
                    JsonPayload.Bool(item, "connected") ?? false,
                    JsonPayload.String(item, "icon")));
            }

            return inputs;
        }, ct);

    public async Task SwitchInputAsync(string inputId, CancellationToken ct)
    {
        var validated = InputValidation.ValidateInputId(inputId);

        var available = await ListInputsAsync(ct).ConfigureAwait(false);
        if (available.Count == 0)
        {
            throw TvException.Unsupported("external input switching");
        }

        var match = available.FirstOrDefault(i => i.Id.Equals(validated, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw TvException.Invalid(
                $"Input '{validated}' is not present on this TV. Available: {string.Join(", ", available.Select(i => i.Id))}.");
        }

        await _session.ExecuteAsync("switch_input", (connection, token) =>
            connection.RequestAsync(SsapUri.SwitchInput, new { inputId = match.Id }, token), ct)
            .ConfigureAwait(false);
    }

    public Task<ChannelInfo> GetCurrentChannelAsync(CancellationToken ct) =>
        _session.ExecuteAsync("get_current_channel", async (connection, token) =>
        {
            var payload = await connection.RequestAsync(SsapUri.GetCurrentChannel, null, token)
                .ConfigureAwait(false);

            return new ChannelInfo(
                JsonPayload.String(payload, "channelId"),
                JsonPayload.String(payload, "channelNumber"),
                JsonPayload.String(payload, "channelName"),
                JsonPayload.String(payload, "programName"));
        }, ct);

    public Task ChannelUpAsync(CancellationToken ct) =>
        _session.ExecuteAsync("channel_up", (connection, token) =>
            connection.RequestAsync(SsapUri.ChannelUp, null, token), ct);

    public Task ChannelDownAsync(CancellationToken ct) =>
        _session.ExecuteAsync("channel_down", (connection, token) =>
            connection.RequestAsync(SsapUri.ChannelDown, null, token), ct);

    public Task TuneChannelAsync(string channelNumber, CancellationToken ct)
    {
        var validated = InputValidation.ValidateChannelNumber(channelNumber);
        return _session.ExecuteAsync("tune_channel", (connection, token) =>
            connection.RequestAsync(SsapUri.OpenChannel, new { channelNumber = validated }, token), ct);
    }

    // --------------------------------------------------------- notifications

    public Task ShowToastAsync(string message, CancellationToken ct)
    {
        var validated = InputValidation.ValidateToastMessage(message);
        return _session.ExecuteAsync("show_toast", (connection, token) =>
            connection.RequestAsync(SsapUri.CreateToast, new { message = validated }, token), ct);
    }

    // ------------------------------------------------------------ power/display

    public Task PowerOffAsync(CancellationToken ct) =>
        _session.ExecuteAsync("power_off", (connection, token) =>
            connection.RequestAsync(SsapUri.TurnOff, null, token), ct);

    public Task ScreenOffAsync(CancellationToken ct) =>
        _session.ExecuteAsync("screen_off", (connection, token) =>
            connection.RequestAsync(SsapUri.TurnOffScreen, null, token), ct);

    public Task ScreenOnAsync(CancellationToken ct) =>
        _session.ExecuteAsync("screen_on", (connection, token) =>
            connection.RequestAsync(SsapUri.TurnOnScreen, null, token), ct);

    private Task StepDelayAsync(CancellationToken ct) =>
        _delay.DelayAsync(TimeSpan.FromMilliseconds(_options.FallbackStepDelayMilliseconds), ct);
}

public enum MediaCommand
{
    Play,
    Pause,
    Stop,
    Rewind,
    FastForward,
}

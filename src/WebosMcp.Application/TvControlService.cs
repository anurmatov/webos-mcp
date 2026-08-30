using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebosMcp.Domain;

namespace WebosMcp.Application;

public sealed record ContentActionResult(ActionPath Path, string Detail, string? AppId = null);

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
    private readonly WebosMcpOptions _options;
    private readonly ILogger<TvControlService> _logger;

    public TvControlService(
        ITvSession session,
        IDelayProvider delay,
        IDialClient dial,
        IOptions<WebosMcpOptions> options,
        ILogger<TvControlService> logger)
    {
        _session = session;
        _delay = delay;
        _dial = dial;
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

        var accepted = await _dial.LaunchAppAsync(
            applicationUrl, DialYouTubeApp, $"v={Uri.EscapeDataString(videoId)}", ct).ConfigureAwait(false);

        if (!accepted)
        {
            throw new TvException(
                TvErrorCode.TvError,
                $"The TV rejected the DIAL launch request for video '{videoId}'.");
        }

        // Acceptance is not evidence. Confirm from the TV's own foreground-app
        // report before calling this a success.
        var evidence = await ConfirmForegroundAsync(
            YouTubeAppIdFragment,
            dialEndpointFound: true,
            dialLaunchAccepted: true,
            started,
            ct).ConfigureAwait(false);

        if (!evidence.ForegroundConfirmed)
        {
            throw new TvException(
                TvErrorCode.TvError,
                $"The DIAL launch for video '{videoId}' was accepted, but YouTube did not reach the " +
                $"foreground within {_options.LaunchVerifyTimeoutSeconds}s " +
                $"(foreground app was '{evidence.ForegroundAppId ?? "unknown"}'). " +
                "Reporting failure rather than an unverified success.");
        }

        return new ContentActionResult(
            ActionPath.Dial,
            $"Launched video '{videoId}' over DIAL and confirmed YouTube reached the foreground " +
            $"(app '{evidence.ForegroundAppId}') after {evidence.ElapsedSeconds:0.0}s.",
            evidence.ForegroundAppId);
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

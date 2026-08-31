using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WebosMcp.Application;

namespace WebosMcp.Server.Tools;

[McpServerToolType]
public sealed class AppTools
{
    private readonly TvControlService _tv;
    private readonly ILogger<AppTools> _logger;

    public AppTools(TvControlService tv, ILogger<AppTools> logger)
    {
        _tv = tv;
        _logger = logger;
    }

    [McpServerTool(Name = "tv_launch_app")]
    [Description("Launch an installed app by its id, as returned by tv_list_apps.")]
    public Task<ToolResult> LaunchApp(
        [Description("App id, for example com.webos.app.browser.")] string appId,
        CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_launch_app", () => _tv.LaunchAppAsync(appId, cancellationToken));

    [McpServerTool(Name = "tv_close_app")]
    [Description("Close a running app by its id.")]
    public Task<ToolResult> CloseApp(
        [Description("App id to close.")] string appId,
        CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_close_app", () => _tv.CloseAppAsync(appId, cancellationToken));

    [McpServerTool(Name = "tv_open_url")]
    [Description(
        "Open an HTTPS URL in the webOS browser. Only HTTPS is accepted — plain HTTP is rejected, " +
        "not silently upgraded. The response states whether a deep link or a fallback sequence ran.")]
    public Task<ToolResult> OpenUrl(
        [Description("Absolute HTTPS URL to open.")] string url,
        CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_open_url", async () =>
        {
            var result = await _tv.OpenUrlAsync(url, cancellationToken);
            return new { path = result.Path.ToString(), detail = result.Detail, appId = result.AppId };
        });

    [McpServerTool(Name = "tv_youtube_search")]
    [Description(
        "NOT SUPPORTED on this TV, and returns TV_UNSUPPORTED_CAPABILITY. YouTube's custom on-screen " +
        "keyboard ignores standard text entry and DIAL exposes no search parameter, so a search cannot be " +
        "confirmed to have run. Use tv_youtube_play with a video id or URL instead.")]
    public Task<ToolResult> YouTubeSearch(
        [Description("Search query.")] string query,
        CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_youtube_search", async () =>
        {
            var result = await _tv.SearchYouTubeAsync(query, cancellationToken);
            return new { path = result.Path.ToString(), detail = result.Detail, appId = result.AppId };
        });

    [McpServerTool(Name = "tv_youtube_play")]
    [Description(
        "Play a specific YouTube video by bare 11-character video id, youtu.be link or youtube.com watch " +
        "URL. Loads the video into the running YouTube receiver over the Lounge protocol and reports " +
        "success only after the receiver itself reports that video id in a Playing state. Works when " +
        "YouTube is already playing something else, and never restarts the app to change video. Returns " +
        "TV_UNSUPPORTED_CAPABILITY when the receiver cannot be controlled.")]
    public Task<ToolResult> YouTubePlay(
        [Description("YouTube video id or URL.")] string video,
        CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_youtube_play", async () =>
        {
            var result = await _tv.PlayYouTubeAsync(video, cancellationToken);
            return new
            {
                path = result.Path.ToString(),
                detail = result.Detail,
                exactVideoConfirmed = result.ExactVideoConfirmed,
                observedVideoId = result.ObservedVideoId,
                observedState = result.ObservedState,
            };
        });

    // ---- YouTube receiver control (Lounge) --------------------------------
    //
    // Every response carries "observed". False means the receiver ACCEPTED the
    // command but announces no event confirming it — do not read that as the
    // action having taken effect.

    [McpServerTool(Name = "tv_youtube_now_playing")]
    [Description(
        "Report what the YouTube receiver says it is playing: video id, player state and position. " +
        "Pure observation — sends no playback command.")]
    public Task<ToolResult> YouTubeNowPlaying(CancellationToken cancellationToken) =>
        Control("tv_youtube_now_playing", () => _tv.YouTubeNowPlayingAsync(cancellationToken));

    [McpServerTool(Name = "tv_youtube_pause")]
    [Description("Pause the YouTube receiver. Succeeds only once the receiver reports a paused state.")]
    public Task<ToolResult> YouTubePause(CancellationToken cancellationToken) =>
        Control("tv_youtube_pause", () => _tv.YouTubePauseAsync(cancellationToken));

    [McpServerTool(Name = "tv_youtube_resume")]
    [Description("Resume the YouTube receiver. Succeeds only once the receiver reports a playing state.")]
    public Task<ToolResult> YouTubeResume(CancellationToken cancellationToken) =>
        Control("tv_youtube_resume", () => _tv.YouTubeResumeAsync(cancellationToken));

    [McpServerTool(Name = "tv_youtube_seek")]
    [Description("Seek the current YouTube video to a position in seconds from the start.")]
    public Task<ToolResult> YouTubeSeek(
        [Description("Position in seconds from the start of the video.")] double seconds,
        CancellationToken cancellationToken) =>
        Control("tv_youtube_seek", () => _tv.YouTubeSeekAsync(seconds, cancellationToken));

    [McpServerTool(Name = "tv_youtube_next")]
    [Description("Skip to the next video in the receiver's queue.")]
    public Task<ToolResult> YouTubeNext(CancellationToken cancellationToken) =>
        Control("tv_youtube_next", () => _tv.YouTubeNextAsync(cancellationToken));

    [McpServerTool(Name = "tv_youtube_previous")]
    [Description("Go back to the previous video in the receiver's queue.")]
    public Task<ToolResult> YouTubePrevious(CancellationToken cancellationToken) =>
        Control("tv_youtube_previous", () => _tv.YouTubePreviousAsync(cancellationToken));

    [McpServerTool(Name = "tv_youtube_queue_add")]
    [Description(
        "Append a video to the YouTube receiver's queue by id or URL. The receiver announces no event " +
        "for this, so the response reports observed=false: the command was accepted, the queue state " +
        "was not read back.")]
    public Task<ToolResult> YouTubeQueueAdd(
        [Description("YouTube video id or URL to append.")] string video,
        CancellationToken cancellationToken) =>
        Control("tv_youtube_queue_add", () => _tv.YouTubeQueueAddAsync(video, cancellationToken));

    [McpServerTool(Name = "tv_youtube_set_receiver_volume")]
    [Description(
        "Set the YouTube receiver's own volume, 0-100. This is the receiver's level, distinct from the " +
        "TV volume set by tv_set_volume. Succeeds only once the receiver reports the change.")]
    public Task<ToolResult> YouTubeSetVolume(
        [Description("Receiver volume, 0-100.")] int volume,
        CancellationToken cancellationToken) =>
        Control("tv_youtube_set_receiver_volume", () => _tv.YouTubeSetVolumeAsync(volume, cancellationToken));

    [McpServerTool(Name = "tv_youtube_set_autoplay")]
    [Description("Enable or disable autoplay on the YouTube receiver. Succeeds only once the receiver reports the new mode.")]
    public Task<ToolResult> YouTubeSetAutoplay(
        [Description("True to enable autoplay, false to disable it.")] bool enabled,
        CancellationToken cancellationToken) =>
        Control("tv_youtube_set_autoplay", () => _tv.YouTubeSetAutoplayAsync(enabled, cancellationToken));

    [McpServerTool(Name = "tv_youtube_set_playback_speed")]
    [Description(
        "Set YouTube playback speed between 0.25 and 2.0. The receiver announces no speed event, so the " +
        "response reports observed=false: accepted, not confirmed.")]
    public Task<ToolResult> YouTubeSetPlaybackSpeed(
        [Description("Playback speed, 0.25 to 2.0.")] double speed,
        CancellationToken cancellationToken) =>
        Control("tv_youtube_set_playback_speed", () => _tv.YouTubeSetPlaybackSpeedAsync(speed, cancellationToken));

    private Task<ToolResult> Control(string name, Func<Task<YouTubeControlResult>> action) =>
        ToolInvoker.RunAsync(_logger, name, async () =>
        {
            var result = await action();
            return new
            {
                command = result.Command,
                observed = result.Observed,
                detail = result.Detail,
                observedVideoId = result.ObservedVideoId,
                observedState = result.ObservedState,
                observedCurrentTime = result.ObservedCurrentTime,
                observedVolume = result.ObservedVolume,
                observedAutoplayEnabled = result.ObservedAutoplayEnabled,
            };
        });
}

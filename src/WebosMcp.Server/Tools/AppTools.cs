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
        "Play a YouTube video by bare 11-character video id, youtu.be link or youtube.com watch URL. " +
        "Launches over DIAL and reports success only after confirming YouTube reached the foreground — " +
        "an accepted launch alone is never reported as success. If YouTube is already running it is " +
        "stopped and cold-started, because a DIAL launch cannot change the video of a running session. " +
        "Returns TV_UNSUPPORTED_CAPABILITY when the TV exposes no DIAL endpoint, YouTube is not " +
        "installed, or a running session cannot be stopped. Note exactVideoConfirmed is always false: " +
        "DIAL cannot report which video is on screen, so the response never claims read-back proof.")]
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
                appId = result.AppId,
                exactVideoConfirmed = result.ExactVideoConfirmed,
                coldStarted = result.ColdStarted,
            };
        });
}

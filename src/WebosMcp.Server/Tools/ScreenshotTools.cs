using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WebosMcp.Application;

namespace WebosMcp.Server.Tools;

/// <summary>
/// A single read-only capture of what is on the screen right now.
///
/// This is the one tool whose success is a native MCP image block rather than the
/// shared JSON envelope, because the envelope is text and the payload is bytes.
/// Failures still use the envelope, so the error contract is unchanged.
///
/// Follows the same selected-device convention as every other tool: one device is
/// active at a time and this takes no device argument.
/// </summary>
[McpServerToolType]
public sealed class ScreenshotTools
{
    private readonly TvControlService _tv;
    private readonly ILogger<ScreenshotTools> _logger;

    public ScreenshotTools(TvControlService tv, ILogger<ScreenshotTools> logger)
    {
        _tv = tv;
        _logger = logger;
    }

    [McpServerTool(Name = "tv_take_screenshot")]
    [Description(
        "Capture the frame currently on the TV screen and return it as an image. Read-only: it changes " +
        "nothing on the TV and the bytes are never written to disk. " +
        "SENSITIVE — a screenshot can show whatever the household is watching, including personal " +
        "content. Invoke it ONLY in direct response to an explicit request from the user right now. " +
        "Never capture proactively, on a schedule, in a loop, or in the background, and never call it to " +
        "'check' the screen on your own initiative. " +
        "A black image is a SUCCESSFUL capture, not a failure: the screen may genuinely be black, or the " +
        "content may be DRM-protected, and neither is distinguishable from the outside. " +
        "Returns TV_UNSUPPORTED_CAPABILITY on models whose firmware does not expose frame capture — it " +
        "is undocumented by LG and not guaranteed on any given set.")]
    public Task<CallToolResult> TakeScreenshot(CancellationToken cancellationToken) =>
        ToolInvoker.RunContentAsync(_logger, "tv_take_screenshot", async () =>
        {
            var screenshot = await _tv.CaptureScreenshotAsync(cancellationToken).ConfigureAwait(false);
            return ToolContent.Image(screenshot.Bytes, screenshot.MimeType);
        });
}

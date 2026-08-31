using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WebosMcp.Application;

namespace WebosMcp.Server.Tools;

[McpServerToolType]
public sealed class NotificationTools
{
    private readonly TvControlService _tv;
    private readonly ILogger<NotificationTools> _logger;

    public NotificationTools(TvControlService tv, ILogger<NotificationTools> logger)
    {
        _tv = tv;
        _logger = logger;
    }

    [McpServerTool(Name = "tv_show_toast")]
    [Description("Show an on-screen toast notification on the TV.")]
    public Task<ToolResult> ShowToast(
        [Description("Message to display, up to 512 characters.")] string message,
        CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_show_toast", () => _tv.ShowToastAsync(message, cancellationToken));
}

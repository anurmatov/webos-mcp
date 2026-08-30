using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WebosMcp.Application;

namespace WebosMcp.Server.Tools;

[McpServerToolType]
public sealed class StatusTools
{
    private readonly TvControlService _tv;
    private readonly ILogger<StatusTools> _logger;

    public StatusTools(TvControlService tv, ILogger<StatusTools> logger)
    {
        _tv = tv;
        _logger = logger;
    }

    [McpServerTool(Name = "tv_get_power_state")]
    [Description("Get the TV's current power state: Active, ScreenOff, Standby, Unreachable or Unknown.")]
    public Task<ToolResult> GetPowerState(CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_get_power_state", async () =>
            new { state = (await _tv.GetPowerStateAsync(cancellationToken)).ToString() });

    [McpServerTool(Name = "tv_get_device_info")]
    [Description("Get the TV's model, firmware and product information.")]
    public Task<ToolResult> GetDeviceInfo(CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_get_device_info", async () =>
        {
            var software = await _tv.GetSoftwareInfoAsync(cancellationToken);
            var system = await _tv.GetSystemInfoAsync(cancellationToken);
            return new { software, system };
        });

    [McpServerTool(Name = "tv_get_foreground_app")]
    [Description("Get the app currently in the foreground on the TV.")]
    public Task<ToolResult> GetForegroundApp(CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_get_foreground_app", async () =>
            await _tv.GetForegroundAppAsync(cancellationToken));

    [McpServerTool(Name = "tv_list_apps")]
    [Description("List the apps installed on the TV, with their launch ids.")]
    public Task<ToolResult> ListApps(CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_list_apps", async () =>
        {
            var apps = await _tv.ListAppsAsync(cancellationToken);
            return new { count = apps.Count, apps };
        });

    [McpServerTool(Name = "tv_get_status")]
    [Description("Get a combined snapshot: power state, foreground app, volume/mute state and current input.")]
    public Task<ToolResult> GetStatus(CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_get_status", async () =>
        {
            var power = await _tv.GetPowerStateAsync(cancellationToken);
            var foreground = await _tv.GetForegroundAppAsync(cancellationToken);
            var volume = await _tv.GetVolumeAsync(cancellationToken);
            return new
            {
                power = power.ToString(),
                foregroundApp = foreground,
                volume,
            };
        });
}

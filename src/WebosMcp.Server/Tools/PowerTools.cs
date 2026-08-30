using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WebosMcp.Application;

namespace WebosMcp.Server.Tools;

[McpServerToolType]
public sealed class PowerTools
{
    private readonly TvControlService _tv;
    private readonly PowerService _power;
    private readonly ILogger<PowerTools> _logger;

    public PowerTools(TvControlService tv, PowerService power, ILogger<PowerTools> logger)
    {
        _tv = tv;
        _power = power;
        _logger = logger;
    }

    [McpServerTool(Name = "tv_power_on")]
    [Description(
        "Wake the TV with a Wake-on-LAN magic packet and verify it reaches an Active state. " +
        "Idempotent: an already-Active TV is a no-op. The result reports whether the Active state was " +
        "actually verified — a sent packet alone is never reported as success.")]
    public Task<ToolResult> PowerOn(CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_power_on", async () =>
            await _power.PowerOnAsync(cancellationToken));

    [McpServerTool(Name = "tv_power_off")]
    [Description("Power the TV off gracefully (standby).")]
    public Task<ToolResult> PowerOff(CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_power_off", () => _tv.PowerOffAsync(cancellationToken));

    [McpServerTool(Name = "tv_screen_off")]
    [Description("Turn the panel off without powering the TV down, where the model supports it.")]
    public Task<ToolResult> ScreenOff(CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_screen_off", () => _tv.ScreenOffAsync(cancellationToken));

    [McpServerTool(Name = "tv_screen_on")]
    [Description("Turn the panel back on after a screen-off.")]
    public Task<ToolResult> ScreenOn(CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_screen_on", () => _tv.ScreenOnAsync(cancellationToken));
}

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WebosMcp.Application;

namespace WebosMcp.Server.Tools;

[McpServerToolType]
public sealed class InputTools
{
    private readonly TvControlService _tv;
    private readonly ILogger<InputTools> _logger;

    public InputTools(TvControlService tv, ILogger<InputTools> logger)
    {
        _tv = tv;
        _logger = logger;
    }

    [McpServerTool(Name = "tv_list_inputs")]
    [Description("List the TV's external inputs (HDMI and similar) with their connected state.")]
    public Task<ToolResult> ListInputs(CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_list_inputs", async () =>
        {
            var inputs = await _tv.ListInputsAsync(cancellationToken);
            return new { count = inputs.Count, inputs };
        });

    [McpServerTool(Name = "tv_switch_input")]
    [Description("Switch to an external input. The id is validated against the inputs the TV reports.")]
    public Task<ToolResult> SwitchInput(
        [Description("Input id, as returned by tv_list_inputs.")] string inputId,
        CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_switch_input", () => _tv.SwitchInputAsync(inputId, cancellationToken));

    [McpServerTool(Name = "tv_get_current_channel")]
    [Description(
        "Get the current channel and programme. Returns TV_UNSUPPORTED_CAPABILITY when the current " +
        "input or model has no tuner information.")]
    public Task<ToolResult> GetCurrentChannel(CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_get_current_channel",
            async () => await _tv.GetCurrentChannelAsync(cancellationToken));

    [McpServerTool(Name = "tv_channel_up")]
    [Description("Move to the next channel. Unsupported on inputs without a tuner.")]
    public Task<ToolResult> ChannelUp(CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_channel_up", () => _tv.ChannelUpAsync(cancellationToken));

    [McpServerTool(Name = "tv_channel_down")]
    [Description("Move to the previous channel. Unsupported on inputs without a tuner.")]
    public Task<ToolResult> ChannelDown(CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_channel_down", () => _tv.ChannelDownAsync(cancellationToken));

    [McpServerTool(Name = "tv_tune_channel")]
    [Description("Tune to a channel number, for example 7 or 7-1. Unsupported on inputs without a tuner.")]
    public Task<ToolResult> TuneChannel(
        [Description("Channel number, digits with an optional major-minor form such as 7-1.")] string channelNumber,
        CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_tune_channel",
            () => _tv.TuneChannelAsync(channelNumber, cancellationToken));
}

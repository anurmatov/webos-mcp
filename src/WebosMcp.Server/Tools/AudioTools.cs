using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WebosMcp.Application;

namespace WebosMcp.Server.Tools;

[McpServerToolType]
public sealed class AudioTools
{
    private readonly TvControlService _tv;
    private readonly ILogger<AudioTools> _logger;

    public AudioTools(TvControlService tv, ILogger<AudioTools> logger)
    {
        _tv = tv;
        _logger = logger;
    }

    [McpServerTool(Name = "tv_get_volume")]
    [Description("Get the current volume, mute state and active sound output.")]
    public Task<ToolResult> GetVolume(CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_get_volume", async () => await _tv.GetVolumeAsync(cancellationToken));

    [McpServerTool(Name = "tv_set_volume")]
    [Description("Set the TV volume. Must be between 0 and 100.")]
    public Task<ToolResult> SetVolume(
        [Description("Volume level, 0-100.")] int volume,
        CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_set_volume", () => _tv.SetVolumeAsync(volume, cancellationToken));

    [McpServerTool(Name = "tv_set_mute")]
    [Description("Mute or unmute the TV.")]
    public Task<ToolResult> SetMute(
        [Description("True to mute, false to unmute.")] bool muted,
        CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_set_mute", () => _tv.SetMuteAsync(muted, cancellationToken));

    [McpServerTool(Name = "tv_list_sound_outputs")]
    [Description("List the sound outputs this TV reports as available.")]
    public Task<ToolResult> ListSoundOutputs(CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_list_sound_outputs", async () =>
        {
            var outputs = await _tv.ListSoundOutputsAsync(cancellationToken);
            return new { count = outputs.Count, outputs };
        });

    [McpServerTool(Name = "tv_set_sound_output")]
    [Description("Switch the audio output. The value is validated against the outputs the TV actually reports.")]
    public Task<ToolResult> SetSoundOutput(
        [Description("Sound output id, as returned by tv_list_sound_outputs.")] string output,
        CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_set_sound_output",
            () => _tv.SetSoundOutputAsync(output, cancellationToken));

    [McpServerTool(Name = "tv_media_control")]
    [Description("Send a media transport command to the foreground app.")]
    public Task<ToolResult> MediaControl(
        [Description("One of: Play, Pause, Stop, Rewind, FastForward.")] MediaCommand command,
        CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_media_control",
            () => _tv.MediaControlAsync(command, cancellationToken));
}

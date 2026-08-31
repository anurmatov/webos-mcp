using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WebosMcp.Application;
using WebosMcp.Domain;

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
    [Description(
        "Get the TV's model, firmware and product information. Partial-result safe: if the TV denies one of " +
        "the two reads — some firmware refuses software information to an unsigned third-party app — the other " +
        "is still returned, the denied field is null, and a 'warnings' entry names the field and the typed " +
        "reason. A TV that is off, unreachable or unpaired still fails the whole call.")]
    public Task<ToolResult> GetDeviceInfo(CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_get_device_info", async () =>
        {
            var partial = new PartialRead();

            var software = await partial.TryAsync(
                "software", () => _tv.GetSoftwareInfoAsync(cancellationToken));
            var system = await partial.TryAsync(
                "system", () => _tv.GetSystemInfoAsync(cancellationToken));

            return new DeviceInfoResponse(software, system, partial.Warnings);
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
    [Description(
        "Get a combined snapshot: power state, foreground app and volume/mute state. Partial-result safe: a " +
        "sub-read the TV denies does not discard the ones that worked — its field is null and a 'warnings' " +
        "entry names the field and the typed reason. A TV that is off, unreachable or unpaired still fails the " +
        "whole call rather than returning a snapshot of nulls.")]
    public Task<ToolResult> GetStatus(CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_get_status", async () =>
        {
            var partial = new PartialRead();

            var power = await partial.TryAsync(
                "power", async () => (await _tv.GetPowerStateAsync(cancellationToken)).ToString());
            var foreground = await partial.TryAsync(
                "foregroundApp", () => _tv.GetForegroundAppAsync(cancellationToken));
            var volume = await partial.TryAsync(
                "volume", async () => (object)await _tv.GetVolumeAsync(cancellationToken));

            return new StatusResponse(power, foreground, volume, partial.Warnings);
        });
}

/// <summary>
/// Field names and order match the pre-partial-result response exactly, and
/// <c>warnings</c> is omitted when empty, so an all-success reply is byte-identical
/// for existing callers. A denied field is present and null rather than absent —
/// a caller can tell "denied" from "not part of this response".
///
/// ⚠️ Every data field carries <see cref="JsonIgnoreCondition.Never"/> ON PURPOSE.
/// The MCP SDK serialises with <c>DefaultIgnoreCondition = WhenWritingNull</c>, so
/// without it a denied field would be silently DROPPED from the response instead
/// of appearing as null — the caller could not distinguish "the TV refused this"
/// from "this tool does not return that". Removing these attributes breaks the
/// contract on the wire while every in-process assertion still passes.
/// </summary>
public sealed record StatusResponse(
    [property: JsonPropertyName("power")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? Power,
    [property: JsonPropertyName("foregroundApp")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    ForegroundApp? ForegroundApp,
    [property: JsonPropertyName("volume")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    object? Volume,
    [property: JsonPropertyName("warnings")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ToolWarning>? Warnings);

/// <summary>See <see cref="StatusResponse"/> — same contract, same guarantees.</summary>
public sealed record DeviceInfoResponse(
    [property: JsonPropertyName("software")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    SoftwareInfo? Software,
    [property: JsonPropertyName("system")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    SystemInfo? System,
    [property: JsonPropertyName("warnings")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ToolWarning>? Warnings);

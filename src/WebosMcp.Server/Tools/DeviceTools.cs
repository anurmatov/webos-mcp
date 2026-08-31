using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WebosMcp.Application;

namespace WebosMcp.Server.Tools;

/// <summary>
/// Finding and selecting the TV, so nobody has to hand-set WEBOSMCP__HOST,
/// MACADDRESS or BROADCASTADDRESS. Discovery locates the TV, registration derives
/// its MAC and broadcast address from the network, and selection takes effect
/// immediately.
///
/// One device is active at a time and no tool takes a device argument — this is
/// device SETUP, not per-call routing. The only step that still needs a person is
/// accepting the pairing prompt on the TV itself, which is a deliberate safety
/// boundary rather than a gap.
/// </summary>
[McpServerToolType]
public sealed class DeviceTools
{
    private readonly DeviceService _devices;
    private readonly ILogger<DeviceTools> _logger;

    public DeviceTools(DeviceService devices, ILogger<DeviceTools> logger)
    {
        _devices = devices;
        _logger = logger;
    }

    [McpServerTool(Name = "tv_discover_devices")]
    [Description(
        "Find webOS TVs. With no argument, scans by SSDP multicast — which does NOT cross a Docker " +
        "bridge network, so in a container pass the TV's address instead to probe it directly. " +
        "Returns candidates with MAC and broadcast address derived where the network can supply them. " +
        "Registers nothing; pass an address to tv_register_device to keep it.")]
    public Task<ToolResult> Discover(
        [Description("Optional TV address to probe directly instead of scanning. Works in a container.")]
        string? host = null,
        CancellationToken cancellationToken = default) =>
        ToolInvoker.RunAsync(_logger, "tv_discover_devices", async () =>
        {
            if (!string.IsNullOrWhiteSpace(host))
            {
                var probed = await _devices.ProbeAsync(host, cancellationToken);

                return new
                {
                    count = probed is null ? 0 : 1,
                    devices = probed is null ? new List<object>() : [Describe(probed)],
                    hint = probed is null
                        ? $"Nothing answered on '{host}'. Check the address and that the TV is on with " +
                          "network control enabled."
                        : null,
                };
            }

            var found = await _devices.DiscoverAsync(cancellationToken);

            return new
            {
                count = found.Devices.Count,
                devices = found.Devices.Select(Describe).ToList(),
                hint = found.Hint,
            };
        });

    [McpServerTool(Name = "tv_register_device")]
    [Description(
        "Register a TV by address and make it the active device. MAC and broadcast address are derived " +
        "from the network where possible, so they normally need not be supplied. Registering an " +
        "already-known address updates it rather than duplicating. Pair with the TV afterwards.")]
    public Task<ToolResult> Register(
        [Description("TV address, for example 192.0.2.10.")] string host,
        [Description("Optional label for this TV.")] string? name = null,
        CancellationToken cancellationToken = default) =>
        ToolInvoker.RunAsync(_logger, "tv_register_device", async () =>
        {
            var device = await _devices.RegisterAsync(host, name, makeActive: true, cancellationToken);

            return new
            {
                device = Describe(device),
                active = true,
                derivedMac = device.MacAddress is not null,
                derivedBroadcast = device.BroadcastAddress is not null,

                // Registration succeeds without a MAC. Saying plainly that only
                // Wake-on-LAN is affected stops it reading as a failed setup.
                wakeOnLanAvailable = device.MacAddress is not null,
                nextStep = device.MacAddress is null
                    ? "Registered. The MAC address could not be derived — this is normal in a container, " +
                      "where the TV is not on the same network segment. Everything works except " +
                      "tv_power_on; supply a MAC with tv_update_device to enable it. Next, run the pair " +
                      "flow and accept the prompt on the TV."
                    : "Registered. Next, run the pair flow and accept the prompt on the TV.",
            };
        });

    [McpServerTool(Name = "tv_list_devices")]
    [Description("List registered TVs and which one is active.")]
    public Task<ToolResult> List(CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_list_devices", async () =>
        {
            var book = await _devices.ListAsync(cancellationToken);

            return new
            {
                activeId = book.ActiveId,
                devices = book.Devices.Select(d => new
                {
                    id = d.Id,
                    host = d.Host,
                    macAddress = d.MacAddress,
                    broadcastAddress = d.BroadcastAddress,
                    friendlyName = d.FriendlyName,
                    modelName = d.ModelName,
                    active = d.Id == book.ActiveId,
                }).ToList(),
            };
        });

    [McpServerTool(Name = "tv_select_device")]
    [Description("Make a registered TV the active one. Takes effect immediately — no restart.")]
    public Task<ToolResult> Select(
        [Description("Device id from tv_list_devices.")] string id,
        CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_select_device", async () =>
            new { device = Describe(await _devices.SelectAsync(id, cancellationToken)), active = true });

    [McpServerTool(Name = "tv_update_device")]
    [Description(
        "Override a registered TV's details. Only needed when derivation got something wrong — for " +
        "example a MAC that could not be read from the network. Omitted fields are left unchanged.")]
    public Task<ToolResult> Update(
        [Description("Device id from tv_list_devices.")] string id,
        [Description("MAC address, for example 00:11:22:33:44:55.")] string? macAddress = null,
        [Description("Broadcast address, for example 192.0.2.255.")] string? broadcastAddress = null,
        [Description("Label for this TV.")] string? name = null,
        CancellationToken cancellationToken = default) =>
        ToolInvoker.RunAsync(_logger, "tv_update_device", async () =>
            new { device = Describe(await _devices.UpdateAsync(id, macAddress, broadcastAddress, name, cancellationToken)) });

    [McpServerTool(Name = "tv_remove_device")]
    [Description(
        "Forget a registered TV. Removing the active device promotes another if one remains. The " +
        "pairing key is not deleted.")]
    public Task<ToolResult> Remove(
        [Description("Device id from tv_list_devices.")] string id,
        CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_remove_device", async () =>
        {
            await _devices.RemoveAsync(id, cancellationToken);
            var book = await _devices.ListAsync(cancellationToken);
            return new { removed = id, activeId = book.ActiveId, remaining = book.Devices.Count };
        });

    private static object Describe(TvDevice device) => new
    {
        id = device.Id,
        host = device.Host,
        macAddress = device.MacAddress,
        broadcastAddress = device.BroadcastAddress,
        friendlyName = device.FriendlyName,
        modelName = device.ModelName,
    };
}

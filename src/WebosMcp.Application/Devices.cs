using WebosMcp.Domain;

namespace WebosMcp.Application;

/// <summary>
/// A TV this server knows how to reach. Stored so an operator never has to hand-set
/// WEBOSMCP__HOST / MACADDRESS / BROADCASTADDRESS: discovery finds the TV, and the
/// address details are derived from the network rather than typed in.
/// </summary>
public sealed record TvDevice(
    string Id,
    string Host,
    string? MacAddress = null,
    string? BroadcastAddress = null,
    string? FriendlyName = null,
    string? ModelName = null);

/// <summary>Everything the store holds: the known devices and which one is active.</summary>
public sealed record DeviceBook(IReadOnlyList<TvDevice> Devices, string? ActiveId)
{
    public static DeviceBook Empty { get; } = new([], null);

    public TvDevice? Active => Devices.FirstOrDefault(d => d.Id == ActiveId);
}

public interface IDeviceStore
{
    Task<DeviceBook> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Persists atomically. Throws KEY_STORAGE_READONLY when the location cannot be written.</summary>
    Task SaveAsync(DeviceBook book, CancellationToken cancellationToken);

    /// <summary>Where the book lives. Never contains a secret — the client key is stored separately.</summary>
    string DescribeLocation();
}

/// <summary>
/// Facts about the local network, abstracted so device registration is testable
/// with no real interfaces or ARP table.
/// </summary>
public interface INetworkFacts
{
    /// <summary>The TV's MAC from the neighbour/ARP table, or null when it cannot be derived.</summary>
    string? TryGetMacAddress(string host);

    /// <summary>The directed broadcast address of the local subnet containing <paramref name="host"/>.</summary>
    string? TryGetBroadcastAddress(string host);

    /// <summary>
    /// Whether a TCP connection to this address and port succeeds. Unlike SSDP, a
    /// unicast TCP connect crosses a Docker bridge network, so this is the discovery
    /// route that actually works in a container.
    /// </summary>
    Task<bool> IsReachableAsync(string host, int port, CancellationToken cancellationToken);
}

/// <param name="Hint">
/// Set when the scan found nothing, explaining what to do instead. A bare empty
/// list reads as "there is no TV", when the real cause is usually that multicast
/// did not leave the container.
/// </param>
public sealed record DeviceDiscoveryResult(IReadOnlyList<TvDevice> Devices, string? Hint = null);

/// <summary>
/// Device registration and selection.
///
/// V1 remains SINGLE-TV in behaviour: exactly one device is active and every tool
/// acts on it. The book exists so that device can be discovered and selected
/// through MCP instead of typed into environment variables — it is not per-call
/// routing, and no tool takes a device argument.
/// </summary>
public sealed class DeviceService
{
    private readonly IDeviceStore _store;
    private readonly ITvDiscovery _discovery;
    private readonly INetworkFacts _network;
    private readonly WebosMcpOptions _options;

    public DeviceService(
        IDeviceStore store,
        ITvDiscovery discovery,
        INetworkFacts network,
        Microsoft.Extensions.Options.IOptions<WebosMcpOptions> options)
    {
        _store = store;
        _discovery = discovery;
        _network = network;
        _options = options.Value;
    }

    /// <summary>
    /// Scans for TVs and enriches each with the address details registration needs,
    /// so the operator picks a device rather than assembling one.
    /// </summary>
    public async Task<DeviceDiscoveryResult> DiscoverAsync(CancellationToken ct)
    {
        var found = await _discovery
            .DiscoverAsync(TimeSpan.FromSeconds(Math.Max(1, _options.DialSsdpTimeoutSeconds)), ct)
            .ConfigureAwait(false);

        var devices = found.Select(tv => Enrich(new TvDevice(
            Id: DeviceId(tv.Address),
            Host: tv.Address,
            FriendlyName: tv.FriendlyName,
            ModelName: tv.ModelName))).ToList();

        // An empty scan is far more often "multicast did not leave the container"
        // than "there is no TV", and reporting a bare empty list sends the operator
        // looking for a fault that is not there.
        var hint = devices.Count > 0
            ? null
            : "No TV answered the SSDP scan. Discovery relies on multicast, which does not cross a " +
              "Docker bridge network — so this is expected in a container unless it runs with " +
              "network_mode: host. Pass the TV's address to this tool to probe it directly, or call " +
              "tv_register_device with the address; neither needs multicast.";

        return new DeviceDiscoveryResult(devices, hint);
    }

    /// <summary>
    /// Checks one address directly. This is the container-reliable route: a unicast
    /// TCP connect crosses a bridge network where SSDP multicast does not.
    /// </summary>
    public async Task<TvDevice?> ProbeAsync(string host, CancellationToken ct)
    {
        var address = Require(host);

        var reachable = await _network
            .IsReachableAsync(address, _options.Port, ct)
            .ConfigureAwait(false);

        return reachable ? Enrich(new TvDevice(DeviceId(address), address)) : null;
    }

    public async Task<DeviceBook> ListAsync(CancellationToken ct) =>
        await _store.LoadAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Registers a device by address, deriving MAC and broadcast where the network
    /// can supply them. Registering the first device makes it active; re-registering
    /// a known host updates it in place rather than duplicating.
    /// </summary>
    public async Task<TvDevice> RegisterAsync(
        string host,
        string? friendlyName,
        bool makeActive,
        CancellationToken ct)
    {
        var address = Require(host);
        var book = await _store.LoadAsync(ct).ConfigureAwait(false);

        var device = Enrich(new TvDevice(DeviceId(address), address, FriendlyName: friendlyName));

        var devices = book.Devices.Where(d => d.Id != device.Id).Append(device).ToList();

        // First device wins by default: with nothing registered there is no
        // meaningful alternative, and leaving it inactive would be a trap.
        var activeId = makeActive || book.ActiveId is null ? device.Id : book.ActiveId;

        await SaveAndApplyAsync(new DeviceBook(devices, activeId), ct).ConfigureAwait(false);
        return device;
    }

    public async Task<TvDevice> SelectAsync(string id, CancellationToken ct)
    {
        var book = await _store.LoadAsync(ct).ConfigureAwait(false);

        var device = book.Devices.FirstOrDefault(d => d.Id == id)
            ?? throw TvException.Invalid($"No registered device has id '{id}'. Use tv_list_devices to see them.");

        await SaveAndApplyAsync(book with { ActiveId = device.Id }, ct).ConfigureAwait(false);
        return device;
    }

    /// <summary>
    /// Overrides derived values. Null leaves a field as it is; only supply these when
    /// derivation got it wrong, which is the point of having them.
    /// </summary>
    public async Task<TvDevice> UpdateAsync(
        string id,
        string? macAddress,
        string? broadcastAddress,
        string? friendlyName,
        CancellationToken ct)
    {
        var book = await _store.LoadAsync(ct).ConfigureAwait(false);

        var existing = book.Devices.FirstOrDefault(d => d.Id == id)
            ?? throw TvException.Invalid($"No registered device has id '{id}'.");

        if (macAddress is not null)
        {
            // Validated here so a typo is rejected at registration rather than
            // surfacing much later as a WOL that silently goes nowhere.
            WebosMcpOptions.ParseMac(macAddress);
        }

        var updated = existing with
        {
            MacAddress = macAddress ?? existing.MacAddress,
            BroadcastAddress = broadcastAddress ?? existing.BroadcastAddress,
            FriendlyName = friendlyName ?? existing.FriendlyName,
        };

        var devices = book.Devices.Select(d => d.Id == id ? updated : d).ToList();

        await SaveAndApplyAsync(book with { Devices = devices }, ct).ConfigureAwait(false);
        return updated;
    }

    public async Task RemoveAsync(string id, CancellationToken ct)
    {
        var book = await _store.LoadAsync(ct).ConfigureAwait(false);

        if (book.Devices.All(d => d.Id != id))
        {
            throw TvException.Invalid($"No registered device has id '{id}'.");
        }

        var devices = book.Devices.Where(d => d.Id != id).ToList();

        // Removing the active device promotes another rather than leaving the server
        // pointed at nothing while still holding devices it could use.
        var activeId = book.ActiveId == id ? devices.FirstOrDefault()?.Id : book.ActiveId;

        await SaveAndApplyAsync(new DeviceBook(devices, activeId), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the active device to the running configuration. Called at startup and
    /// after every mutation.
    ///
    /// Explicit environment configuration WINS: an operator who set WEBOSMCP__HOST
    /// meant it, and silently overriding it from a stored book would be the kind of
    /// invisible precedence that costs an afternoon to debug.
    /// </summary>
    public async Task<TvDevice?> ApplyActiveAsync(CancellationToken ct)
    {
        DeviceBook book;

        try
        {
            book = await _store.LoadAsync(ct).ConfigureAwait(false);
        }
        catch (TvException)
        {
            // An unreadable book must not stop the server starting; explicit
            // environment configuration may well be all that is needed.
            return null;
        }

        var active = book.Active;
        if (active is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(_options.Host) || _options.HostCameFromDeviceBook)
        {
            _options.Host = active.Host;
            _options.HostCameFromDeviceBook = true;
        }

        if (string.IsNullOrWhiteSpace(_options.MacAddress) && active.MacAddress is { Length: > 0 })
        {
            _options.MacAddress = active.MacAddress;
        }

        if (active.BroadcastAddress is { Length: > 0 } &&
            _options.BroadcastAddress == WebosMcpOptions.DefaultBroadcastAddress)
        {
            _options.BroadcastAddress = active.BroadcastAddress;
        }

        return active;
    }

    /// <summary>
    /// Adds derived address details. Derivation is a convenience and is wrapped
    /// here as well as in the provider: an implementation that throws — the
    /// container image with no ping binary did — must not be able to stop a device
    /// being registered.
    /// </summary>
    private TvDevice Enrich(TvDevice device)
    {
        string? mac = null;
        string? broadcast = null;

        try
        {
            mac = _network.TryGetMacAddress(device.Host);
        }
        catch (Exception)
        {
            // Undeterminable, not fatal.
        }

        try
        {
            broadcast = _network.TryGetBroadcastAddress(device.Host);
        }
        catch (Exception)
        {
        }

        return device with
        {
            MacAddress = device.MacAddress ?? mac,
            BroadcastAddress = device.BroadcastAddress ?? broadcast,
        };
    }

    private async Task SaveAndApplyAsync(DeviceBook book, CancellationToken ct)
    {
        await _store.SaveAsync(book, ct).ConfigureAwait(false);

        // A selection that does not take effect until restart would be a worse trap
        // than the environment variables this replaces.
        _options.HostCameFromDeviceBook = true;
        _options.Host = null;
        await ApplyActiveAsync(ct).ConfigureAwait(false);
    }

    private static string Require(string host) =>
        string.IsNullOrWhiteSpace(host)
            ? throw TvException.Invalid("A device address is required, for example 192.0.2.10.")
            : host.Trim();

    /// <summary>The address is the identity: one TV per address, so re-registering updates.</summary>
    private static string DeviceId(string host) => host.Trim().ToLowerInvariant();
}

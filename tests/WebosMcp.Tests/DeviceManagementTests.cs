using Microsoft.Extensions.Options;
using WebosMcp.Application;
using WebosMcp.Domain;
using WebosMcp.Infrastructure;
using Xunit;

namespace WebosMcp.Tests;

/// <summary>
/// Device discovery, registration and selection through MCP, so an operator never
/// hand-sets WEBOSMCP__HOST / MACADDRESS / BROADCASTADDRESS.
///
/// One device is active at a time and no tool takes a device argument: this is
/// device SETUP, not per-call routing. The only step that still needs a person is
/// accepting the pairing prompt on the TV.
/// </summary>
public sealed class DeviceManagementTests
{
    private sealed class InMemoryDeviceStore : IDeviceStore
    {
        public DeviceBook Book { get; set; } = DeviceBook.Empty;

        public int Saves { get; private set; }

        public Task<DeviceBook> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Book);

        public Task SaveAsync(DeviceBook book, CancellationToken cancellationToken)
        {
            Saves++;
            Book = book;
            return Task.CompletedTask;
        }

        public string DescribeLocation() => "(in memory)";
    }

    private sealed class FakeDiscovery : ITvDiscovery
    {
        public List<DiscoveredTv> Found { get; } = [];

        public Task<IReadOnlyList<DiscoveredTv>> DiscoverAsync(TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DiscoveredTv>>(Found);
    }

    private sealed class FakeNetworkFacts : INetworkFacts
    {
        public string? Mac { get; set; } = "00:11:22:33:44:55";

        public string? Broadcast { get; set; } = "192.0.2.255";

        public string? TryGetMacAddress(string host) => Mac;

        public string? TryGetBroadcastAddress(string host) => Broadcast;
    }

    private static (DeviceService Service, InMemoryDeviceStore Store, FakeDiscovery Discovery,
        FakeNetworkFacts Network, WebosMcpOptions Options) Build()
    {
        var store = new InMemoryDeviceStore();
        var discovery = new FakeDiscovery();
        var network = new FakeNetworkFacts();
        var options = new WebosMcpOptions();

        return (new DeviceService(store, discovery, network, Options.Create(options)),
            store, discovery, network, options);
    }

    // ---- discovery supplies what registration needs ------------------------

    [Fact]
    public async Task Discovery_returns_candidates_enriched_with_derived_address_details()
    {
        var (service, _, discovery, _, _) = Build();
        discovery.Found.Add(new DiscoveredTv("192.0.2.10", "Living Room", "OLED55"));

        var device = Assert.Single(await service.DiscoverAsync(CancellationToken.None));

        Assert.Equal("192.0.2.10", device.Host);
        Assert.Equal("00:11:22:33:44:55", device.MacAddress);
        Assert.Equal("192.0.2.255", device.BroadcastAddress);
        Assert.Equal("Living Room", device.FriendlyName);
    }

    [Fact]
    public async Task Discovery_registers_nothing_by_itself()
    {
        // Scanning must not mutate configuration — the operator chooses.
        var (service, store, discovery, _, _) = Build();
        discovery.Found.Add(new DiscoveredTv("192.0.2.10", null, null));

        await service.DiscoverAsync(CancellationToken.None);

        Assert.Empty(store.Book.Devices);
        Assert.Equal(0, store.Saves);
    }

    // ---- registration derives instead of asking ----------------------------

    [Fact]
    public async Task Registering_derives_the_mac_and_broadcast_and_activates_the_device()
    {
        var (service, store, _, _, options) = Build();

        var device = await service.RegisterAsync("192.0.2.10", "Living Room", true, CancellationToken.None);

        Assert.Equal("00:11:22:33:44:55", device.MacAddress);
        Assert.Equal("192.0.2.255", device.BroadcastAddress);
        Assert.Equal(device.Id, store.Book.ActiveId);

        // The point of the whole feature: the running config now knows the TV
        // without anyone setting an environment variable.
        Assert.Equal("192.0.2.10", options.Host);
        Assert.Equal("00:11:22:33:44:55", options.MacAddress);
        Assert.Equal("192.0.2.255", options.BroadcastAddress);
    }

    [Fact]
    public async Task A_device_the_network_cannot_describe_still_registers()
    {
        // Undeterminable is not a failure — WOL simply stays unavailable until the
        // operator supplies a MAC, and the response says which fields were derived.
        var (service, _, _, network, options) = Build();
        network.Mac = null;
        network.Broadcast = null;

        var device = await service.RegisterAsync("192.0.2.10", null, true, CancellationToken.None);

        Assert.Null(device.MacAddress);
        Assert.Equal("192.0.2.10", options.Host);
        Assert.Equal(WebosMcpOptions.DefaultBroadcastAddress, options.BroadcastAddress);
    }

    [Fact]
    public async Task Registering_a_known_address_updates_it_rather_than_duplicating()
    {
        var (service, store, _, _, _) = Build();

        await service.RegisterAsync("192.0.2.10", "First", true, CancellationToken.None);
        await service.RegisterAsync("192.0.2.10", "Renamed", true, CancellationToken.None);

        var device = Assert.Single(store.Book.Devices);
        Assert.Equal("Renamed", device.FriendlyName);
    }

    [Fact]
    public async Task The_first_device_registered_becomes_active_even_when_not_asked_for()
    {
        // With nothing registered there is no meaningful alternative, and leaving it
        // inactive would be a trap.
        var (service, store, _, _, _) = Build();

        await service.RegisterAsync("192.0.2.10", null, makeActive: false, CancellationToken.None);

        Assert.NotNull(store.Book.ActiveId);
    }

    // ---- explicit environment configuration wins ---------------------------

    [Fact]
    public async Task An_explicitly_configured_host_is_not_overridden_by_the_stored_device()
    {
        // Silently overriding what an operator typed is the kind of invisible
        // precedence that costs an afternoon.
        var store = new InMemoryDeviceStore
        {
            Book = new DeviceBook([new TvDevice("192.0.2.99", "192.0.2.99")], "192.0.2.99"),
        };

        var options = new WebosMcpOptions { Host = "192.0.2.10" };

        var service = new DeviceService(
            store, new FakeDiscovery(), new FakeNetworkFacts(), Options.Create(options));

        await service.ApplyActiveAsync(CancellationToken.None);

        Assert.Equal("192.0.2.10", options.Host);
    }

    [Fact]
    public async Task An_explicitly_configured_mac_is_not_overridden_either()
    {
        var store = new InMemoryDeviceStore
        {
            Book = new DeviceBook(
                [new TvDevice("192.0.2.99", "192.0.2.99", MacAddress: "AA:BB:CC:DD:EE:FF")],
                "192.0.2.99"),
        };

        var options = new WebosMcpOptions { MacAddress = "00:11:22:33:44:55" };

        var service = new DeviceService(
            store, new FakeDiscovery(), new FakeNetworkFacts(), Options.Create(options));

        await service.ApplyActiveAsync(CancellationToken.None);

        Assert.Equal("00:11:22:33:44:55", options.MacAddress);
    }

    // ---- selection, update, removal ----------------------------------------

    [Fact]
    public async Task Selecting_a_device_takes_effect_immediately_without_a_restart()
    {
        // A selection that needed a restart would be a worse trap than the env vars
        // this replaces.
        var (service, _, _, _, options) = Build();

        await service.RegisterAsync("192.0.2.10", null, true, CancellationToken.None);
        await service.RegisterAsync("192.0.2.20", null, true, CancellationToken.None);
        await service.SelectAsync("192.0.2.10", CancellationToken.None);

        Assert.Equal("192.0.2.10", options.Host);
    }

    [Fact]
    public async Task Selecting_an_unknown_device_is_rejected()
    {
        var (service, _, _, _, _) = Build();

        var error = await Assert.ThrowsAsync<TvException>(
            () => service.SelectAsync("nope", CancellationToken.None));

        Assert.Equal(TvErrorCode.InvalidInput, error.Code);
    }

    [Fact]
    public async Task An_override_replaces_a_wrongly_derived_value_and_leaves_the_rest()
    {
        var (service, _, _, _, _) = Build();
        await service.RegisterAsync("192.0.2.10", "Living Room", true, CancellationToken.None);

        var updated = await service.UpdateAsync(
            "192.0.2.10", "AA:BB:CC:DD:EE:FF", null, null, CancellationToken.None);

        Assert.Equal("AA:BB:CC:DD:EE:FF", updated.MacAddress);
        Assert.Equal("192.0.2.255", updated.BroadcastAddress);   // untouched
        Assert.Equal("Living Room", updated.FriendlyName);       // untouched
    }

    [Fact]
    public async Task A_malformed_mac_override_is_rejected_at_the_point_of_entry()
    {
        // Otherwise the typo surfaces much later as a WOL that silently goes nowhere.
        var (service, _, _, _, _) = Build();
        await service.RegisterAsync("192.0.2.10", null, true, CancellationToken.None);

        var error = await Assert.ThrowsAsync<TvException>(
            () => service.UpdateAsync("192.0.2.10", "not-a-mac", null, null, CancellationToken.None));

        Assert.Equal(TvErrorCode.InvalidInput, error.Code);
    }

    [Fact]
    public async Task Removing_the_active_device_promotes_another_rather_than_leaving_none()
    {
        var (service, store, _, _, _) = Build();
        await service.RegisterAsync("192.0.2.10", null, true, CancellationToken.None);
        await service.RegisterAsync("192.0.2.20", null, true, CancellationToken.None);

        await service.RemoveAsync("192.0.2.20", CancellationToken.None);

        Assert.Equal("192.0.2.10", store.Book.ActiveId);
    }

    [Fact]
    public async Task Removing_the_last_device_leaves_nothing_active()
    {
        var (service, store, _, _, _) = Build();
        await service.RegisterAsync("192.0.2.10", null, true, CancellationToken.None);

        await service.RemoveAsync("192.0.2.10", CancellationToken.None);

        Assert.Null(store.Book.ActiveId);
        Assert.Empty(store.Book.Devices);
    }

    [Fact]
    public async Task Removing_an_unknown_device_is_rejected()
    {
        var (service, _, _, _, _) = Build();

        await Assert.ThrowsAsync<TvException>(() => service.RemoveAsync("nope", CancellationToken.None));
    }

    // ---- deriving the address details --------------------------------------

    [Theory]
    [InlineData("192.0.2.10 dev eth0 lladdr 00:11:22:33:44:55 REACHABLE", "192.0.2.10", "00:11:22:33:44:55")]
    [InlineData("? (192.0.2.10) at 00:11:22:33:44:55 [ether] on eth0", "192.0.2.10", "00:11:22:33:44:55")]
    public void A_mac_is_read_out_of_the_neighbour_table(string line, string address, string expected) =>
        Assert.Equal(expected, SystemNetworkFacts.ParseMacForAddress(line, address));

    [Fact]
    public void A_prefix_of_another_address_is_not_matched()
    {
        // 192.0.2.1 must not match the entry for 192.0.2.10, which a substring
        // search would do — and the wrong MAC means WOL wakes nothing.
        const string table = "192.0.2.10 dev eth0 lladdr 00:11:22:33:44:55 REACHABLE";

        Assert.Null(SystemNetworkFacts.ParseMacForAddress(table, "192.0.2.1"));
    }

    [Fact]
    public void An_address_with_no_entry_yields_null_rather_than_a_guess() =>
        Assert.Null(SystemNetworkFacts.ParseMacForAddress(
            "192.0.2.20 dev eth0 lladdr 00:11:22:33:44:55 REACHABLE", "192.0.2.10"));

    [Theory]
    [InlineData("192.0.2.10", "255.255.255.0", "192.0.2.255")]
    [InlineData("10.1.2.3", "255.255.0.0", "10.1.255.255")]
    [InlineData("172.16.5.9", "255.255.255.128", "172.16.5.127")]
    public void The_broadcast_address_is_derived_from_the_mask(string address, string mask, string expected) =>
        Assert.Equal(expected, SystemNetworkFacts.Broadcast(
            System.Net.IPAddress.Parse(address), System.Net.IPAddress.Parse(mask)));

    [Theory]
    [InlineData("192.0.2.5", "192.0.2.10", "255.255.255.0", true)]
    [InlineData("192.0.2.5", "198.51.100.10", "255.255.255.0", false)]
    public void Subnet_membership_decides_which_interface_supplies_the_broadcast(
        string a, string b, string mask, bool expected) =>
        Assert.Equal(expected, SystemNetworkFacts.SameSubnet(
            System.Net.IPAddress.Parse(a),
            System.Net.IPAddress.Parse(b),
            System.Net.IPAddress.Parse(mask)));
}

using System.Net.NetworkInformation;
using WebosMcp.Domain;
using WebosMcp.Infrastructure;
using WebosMcp.Tests.Fakes;
using Xunit;

namespace WebosMcp.Tests;

/// <summary>Wake-on-LAN idempotency, post-WOL verification, and the broadcast + unicast send path.</summary>
public sealed class PowerOnTests
{
    private static FakeSsapConnection TvReporting(string state)
    {
        var connection = new FakeSsapConnection();
        connection.Respond(
            "ssap://com.webos.service.tvpower/power/getPowerState",
            $$"""{"returnValue":true,"state":"{{state}}"}""");
        return connection;
    }

    [Fact]
    public async Task An_already_active_tv_is_a_verified_no_op()
    {
        var harness = new TestHarness(TvReporting("Active"));

        var result = await harness.Power.PowerOnAsync(CancellationToken.None);

        Assert.True(result.Verified);
        Assert.True(result.AlreadyOn);
        Assert.Equal(PowerState.Active, result.FinalState);
        Assert.Equal(0, result.MagicPacketsSent);

        // Idempotent: no redundant packet.
        Assert.Empty(harness.Wol.Sends);
    }

    [Fact]
    public async Task Calling_power_on_twice_against_an_active_tv_stays_a_no_op()
    {
        var harness = new TestHarness(TvReporting("Active"));

        await harness.Power.PowerOnAsync(CancellationToken.None);
        var second = await harness.Power.PowerOnAsync(CancellationToken.None);

        Assert.True(second.Verified);
        Assert.True(second.AlreadyOn);
        Assert.Empty(harness.Wol.Sends);
    }

    [Fact]
    public async Task A_sleeping_tv_that_wakes_is_reported_as_verified()
    {
        // Off on the first probe, Active afterwards.
        var sleeping = new FakeSsapConnection { ConnectFailure = TvException.Off() };
        var awake = TvReporting("Active");

        var harness = new TestHarness(sleeping);
        harness.Factory.Enqueue(awake);

        var result = await harness.Power.PowerOnAsync(CancellationToken.None);

        Assert.True(result.Verified);
        Assert.False(result.AlreadyOn);
        Assert.Equal(PowerState.Active, result.FinalState);
        Assert.Single(harness.Wol.Sends);
    }

    [Fact]
    public async Task A_tv_that_never_wakes_is_reported_as_UNVERIFIED_not_success()
    {
        var connection = new FakeSsapConnection { ConnectFailure = TvException.Unreachable("no route") };
        var harness = new TestHarness(connection, options =>
        {
            options.PowerOnVerifyTimeoutSeconds = 9;
            options.PowerOnPollIntervalSeconds = 3;
        });

        // Every probe fails.
        for (var i = 0; i < 10; i++)
        {
            harness.Factory.Enqueue(new FakeSsapConnection
            {
                ConnectFailure = TvException.Unreachable("no route"),
            });
        }

        var result = await harness.Power.PowerOnAsync(CancellationToken.None);

        Assert.False(result.Verified);
        Assert.Equal(PowerState.Unreachable, result.FinalState);
        Assert.Contains("UNVERIFIED", result.Detail, StringComparison.Ordinal);
        Assert.Single(harness.Wol.Sends);
    }

    [Fact]
    public async Task The_verification_poll_is_bounded_by_the_configured_budget()
    {
        var connection = new FakeSsapConnection { ConnectFailure = TvException.Off() };
        var harness = new TestHarness(connection, options =>
        {
            options.PowerOnVerifyTimeoutSeconds = 12;
            options.PowerOnPollIntervalSeconds = 3;
        });

        for (var i = 0; i < 20; i++)
        {
            harness.Factory.Enqueue(new FakeSsapConnection { ConnectFailure = TvException.Off() });
        }

        await harness.Power.PowerOnAsync(CancellationToken.None);

        // 12s budget at a 3s interval is four polls — not an unbounded spin.
        Assert.Equal(4, harness.Delay.Count);
    }

    [Fact]
    public void Wol_targets_include_the_subnet_broadcast_and_the_unicast_fallback()
    {
        var harness = new TestHarness(configure: options =>
        {
            options.Host = "192.0.2.10";
            options.BroadcastAddress = "192.0.2.255";
        });

        var targets = harness.Power.BuildTargets().Select(t => t.ToString()).ToList();

        Assert.Contains("192.0.2.255:9", targets);

        // The directed unicast leg: the documented best-effort fallback for
        // bridge-mode container deployments where the broadcast cannot escape.
        Assert.Contains("192.0.2.10:9", targets);
        Assert.Equal(2, targets.Count);
    }

    [Fact]
    public async Task Both_legs_are_actually_written_to_and_reported()
    {
        var connection = new FakeSsapConnection { ConnectFailure = TvException.Off() };
        var harness = new TestHarness(connection, options => options.PowerOnVerifyTimeoutSeconds = 3);

        for (var i = 0; i < 5; i++)
        {
            harness.Factory.Enqueue(new FakeSsapConnection { ConnectFailure = TvException.Off() });
        }

        var result = await harness.Power.PowerOnAsync(CancellationToken.None);

        Assert.Equal(2, result.MagicPacketsSent);
        Assert.Equal(["192.0.2.255:9", "192.0.2.10:9"], result.SentTo);
        Assert.Equal("001122334455", harness.Wol.Sends.Single().Mac);
    }

    [Fact]
    public async Task Power_on_without_a_configured_mac_is_an_input_error()
    {
        var connection = new FakeSsapConnection { ConnectFailure = TvException.Off() };
        var harness = new TestHarness(connection, options => options.MacAddress = null);

        var ex = await Assert.ThrowsAsync<TvException>(
            () => harness.Power.PowerOnAsync(CancellationToken.None));

        Assert.Equal(TvErrorCode.InvalidInput, ex.Code);
        Assert.Empty(harness.Wol.Sends);
    }

    [Fact]
    public void The_magic_packet_is_six_ff_bytes_then_sixteen_mac_repetitions()
    {
        var mac = PhysicalAddress.Parse("00-11-22-33-44-55");
        var packet = UdpWolSender.BuildMagicPacket(mac);

        Assert.Equal(102, packet.Length);
        Assert.All(packet[..6], b => Assert.Equal(0xFF, b));

        for (var repetition = 0; repetition < 16; repetition++)
        {
            Assert.Equal(mac.GetAddressBytes(), packet[(6 + (repetition * 6))..(12 + (repetition * 6))]);
        }
    }
}

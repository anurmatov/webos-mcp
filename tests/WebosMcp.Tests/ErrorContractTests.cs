using WebosMcp.Domain;
using WebosMcp.Infrastructure;
using WebosMcp.Tests.Fakes;
using Xunit;

namespace WebosMcp.Tests;

/// <summary>
/// The four-way error contract. Each case gets a dedicated test, because the
/// whole point is that a caller can tell them apart without string-matching.
/// </summary>
public sealed class ErrorContractTests
{
    [Fact]
    public async Task Unpaired_tv_returns_PAIRING_REQUIRED_before_any_connection_attempt()
    {
        var harness = new TestHarness();
        harness.KeyStore.Current = null;

        var ex = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.GetPowerStateAsync(CancellationToken.None));

        Assert.Equal(TvErrorCode.PairingRequired, ex.Code);
        Assert.Equal("PAIRING_REQUIRED", ex.Code.ToWireCode());

        // Fails closed: the pairing gate runs before the network is touched, so
        // "not paired" can never be masked by the TV happening to be off.
        Assert.Equal(0, harness.Factory.CreateCount);
    }

    [Fact]
    public async Task Powered_off_tv_returns_TV_OFF()
    {
        var connection = new FakeSsapConnection { ConnectFailure = TvException.Off() };
        var harness = new TestHarness(connection);

        var ex = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.GetPowerStateAsync(CancellationToken.None));

        Assert.Equal(TvErrorCode.TvOff, ex.Code);
        Assert.Equal("TV_OFF", ex.Code.ToWireCode());
    }

    [Fact]
    public async Task Unreachable_tv_returns_TV_UNREACHABLE()
    {
        var connection = new FakeSsapConnection
        {
            ConnectFailure = TvException.Unreachable("no route to host"),
        };
        var harness = new TestHarness(connection);

        var ex = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.GetPowerStateAsync(CancellationToken.None));

        Assert.Equal(TvErrorCode.TvUnreachable, ex.Code);
        Assert.Equal("TV_UNREACHABLE", ex.Code.ToWireCode());
    }

    [Fact]
    public async Task Missing_capability_returns_TV_UNSUPPORTED_CAPABILITY()
    {
        var connection = new FakeSsapConnection();
        connection.Fail("ssap://tv/getCurrentChannel", TvException.Unsupported("channel information"));

        var harness = new TestHarness(connection);

        var ex = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.GetCurrentChannelAsync(CancellationToken.None));

        Assert.Equal(TvErrorCode.TvUnsupportedCapability, ex.Code);
        Assert.Equal("TV_UNSUPPORTED_CAPABILITY", ex.Code.ToWireCode());
    }

    [Fact]
    public void All_four_contract_codes_have_distinct_wire_values()
    {
        var codes = new[]
        {
            TvErrorCode.PairingRequired,
            TvErrorCode.TvOff,
            TvErrorCode.TvUnreachable,
            TvErrorCode.TvUnsupportedCapability,
        }.Select(c => c.ToWireCode()).ToArray();

        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(["PAIRING_REQUIRED", "TV_OFF", "TV_UNREACHABLE", "TV_UNSUPPORTED_CAPABILITY"], codes);
    }

    [Theory]
    [InlineData("403 access denied", TvErrorCode.PairingRequired)]
    [InlineData("Client is not registered", TvErrorCode.PairingRequired)]
    [InlineData("404 no such service or method", TvErrorCode.TvUnsupportedCapability)]
    [InlineData("This feature is not supported", TvErrorCode.TvUnsupportedCapability)]
    [InlineData("something else went wrong", TvErrorCode.TvError)]
    public void Ssap_error_text_maps_to_the_right_contract_code(string detail, TvErrorCode expected)
    {
        Assert.Equal(expected, SsapWebSocketConnection.MapSsapError(detail).Code);
    }

    [Fact]
    public async Task A_rejected_stored_key_surfaces_as_PAIRING_REQUIRED_not_a_generic_error()
    {
        var connection = new FakeSsapConnection { RegisterFailure = TvException.PairingRequired() };
        var harness = new TestHarness(connection);

        var ex = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.GetPowerStateAsync(CancellationToken.None));

        Assert.Equal(TvErrorCode.PairingRequired, ex.Code);
    }
}

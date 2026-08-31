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
    public void The_contract_codes_have_distinct_wire_values()
    {
        var codes = new[]
        {
            TvErrorCode.PairingRequired,
            TvErrorCode.TvOff,
            TvErrorCode.TvUnreachable,
            TvErrorCode.TvUnsupportedCapability,
            TvErrorCode.TvPermissionDenied,
        }.Select(c => c.ToWireCode()).ToArray();

        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            [
                "PAIRING_REQUIRED",
                "TV_OFF",
                "TV_UNREACHABLE",
                "TV_UNSUPPORTED_CAPABILITY",
                "TV_PERMISSION_DENIED",
            ],
            codes);
    }

    /// <summary>
    /// A COMMAND frame. The session is registered and other commands work, so an
    /// authorization refusal is about this capability — never about the key.
    /// </summary>
    [Theory]
    [InlineData("401 insufficient permissions", TvErrorCode.TvPermissionDenied)]
    [InlineData("403 access denied", TvErrorCode.TvPermissionDenied)]
    [InlineData("Permission denied", TvErrorCode.TvPermissionDenied)]
    [InlineData("unauthorized", TvErrorCode.TvPermissionDenied)]
    [InlineData("404 no such service or method", TvErrorCode.TvUnsupportedCapability)]
    [InlineData("This feature is not supported", TvErrorCode.TvUnsupportedCapability)]
    [InlineData("something else went wrong", TvErrorCode.TvError)]
    public void Request_frame_errors_map_to_the_right_contract_code(string detail, TvErrorCode expected)
    {
        Assert.Equal(expected, SsapWebSocketConnection.MapRequestError(detail).Code);
    }

    /// <summary>
    /// A REGISTRATION frame. Here the same wording really does mean the supplied
    /// key was refused, and PAIRING_REQUIRED is correct.
    /// </summary>
    [Theory]
    [InlineData("403 access denied", TvErrorCode.PairingRequired)]
    [InlineData("Client is not registered", TvErrorCode.PairingRequired)]
    [InlineData("registration failed", TvErrorCode.PairingRequired)]
    [InlineData("401 insufficient permissions", TvErrorCode.PairingRequired)]
    [InlineData("404 no such service or method", TvErrorCode.TvUnsupportedCapability)]
    [InlineData("something else went wrong", TvErrorCode.TvError)]
    public void Registration_frame_errors_map_to_the_right_contract_code(string detail, TvErrorCode expected)
    {
        Assert.Equal(expected, SsapWebSocketConnection.MapRegistrationError(detail).Code);
    }

    [Fact]
    public void A_denied_command_is_never_reported_as_a_missing_key()
    {
        // The regression this whole split exists for. tv_close_app returned
        // "No valid client key" immediately after another SSAP call succeeded on
        // the same registered session — sending an operator to re-pair a pairing
        // that was never broken.
        foreach (var detail in new[]
        {
            "403 access denied",
            "401 insufficient permissions",
            "Permission denied for this app",
        })
        {
            var mapped = SsapWebSocketConnection.MapRequestError(detail);

            Assert.NotEqual(TvErrorCode.PairingRequired, mapped.Code);
            Assert.Equal(TvErrorCode.TvPermissionDenied, mapped.Code);

            // The TV's own wording survives: it is what distinguishes one denied
            // capability from another.
            Assert.Contains(detail, mapped.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("No valid client key", mapped.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void No_authorization_wording_still_falls_through_to_PAIRING_REQUIRED_on_a_command()
    {
        // Guards the half-fixed state the issue calls out as worse than the status
        // quo: every authorization refusal on a command frame must be covered, not
        // just the two that were observed.
        string[] refusals =
        [
            "401", "403", "denied", "Access Denied", "insufficient permissions",
            "Unauthorized", "forbidden",
        ];

        Assert.All(
            refusals,
            detail => Assert.Equal(
                TvErrorCode.TvPermissionDenied, SsapWebSocketConnection.MapRequestError(detail).Code));
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

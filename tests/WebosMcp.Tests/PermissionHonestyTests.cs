using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebosMcp.Application;
using WebosMcp.Domain;
using WebosMcp.Infrastructure;
using WebosMcp.Server.Tools;
using WebosMcp.Tests.Fakes;
using Xunit;

namespace WebosMcp.Tests;

/// <summary>
/// Honesty across permission denials.
///
/// The defects these cover shared one shape: the server knew something true and
/// reported something else. A denied command was reported as a lost key; an
/// aggregate read that partly worked was reported as a total failure. Both told a
/// caller to fix the wrong thing.
/// </summary>
public sealed class PermissionHonestyTests : IDisposable
{
    private const string ForegroundAppUri = "ssap://com.webos.applicationManager/getForegroundAppInfo";
    private const string PowerStateUri = "ssap://com.webos.service.tvpower/power/getPowerState";
    private const string VolumeUri = "ssap://audio/getVolume";
    private const string SoftwareInfoUri = "ssap://com.webos.service.update/getCurrentSWInformation";
    private const string SystemInfoUri = "ssap://system/getSystemInfo";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "webos-mcp-perm-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ------------------------------------------------------------- fixtures

    /// <summary>A TV where power and volume answer normally. Synthetic, never a real capture.</summary>
    private static FakeSsapConnection HealthyTv()
    {
        var connection = new FakeSsapConnection();
        connection.Respond(PowerStateUri, """{"returnValue":true,"state":"Active"}""");
        connection.Respond(VolumeUri, """{"returnValue":true,"volume":12,"muted":false}""");
        connection.Respond(SystemInfoUri, """{"returnValue":true,"modelName":"SYNTHETIC","receiverType":"synthetic"}""");
        connection.Respond(
            SoftwareInfoUri,
            """{"returnValue":true,"product_name":"synthetic","major_ver":"01","minor_ver":"02"}""");
        connection.Respond(
            ForegroundAppUri,
            """{"returnValue":true,"appId":"com.example.synthetic","windowId":"","processId":""}""");
        return connection;
    }

    private static StatusTools ToolsFor(TestHarness harness) =>
        new(harness.Control, harness.LoggerFactory.CreateLogger<StatusTools>());

    /// <summary>
    /// Serialised with the MCP SDK's OWN options, not the framework defaults.
    /// Asserting against default options would pin PascalCase field names that no
    /// client ever sees, and would pass while the wire shape was wrong.
    /// </summary>
    private static JsonElement Json(ToolResult result) =>
        JsonSerializer.SerializeToElement(result.Result, McpJsonUtilities.DefaultOptions);

    private static ToolWarning[] WarningsOf(ToolResult result)
    {
        var json = Json(result);
        return json.TryGetProperty("warnings", out var warnings)
            ? warnings.EnumerateArray()
                .Select(w => new ToolWarning(
                    w.GetProperty("field").GetString()!,
                    w.GetProperty("code").GetString()!,
                    w.GetProperty("message").GetString()!))
                .ToArray()
            : [];
    }

    // ------------------------------------------ aggregate: command-level denial

    [Theory]
    [InlineData("TV_PERMISSION_DENIED")]
    [InlineData("TV_UNSUPPORTED_CAPABILITY")]
    [InlineData("TV_ERROR")]
    public async Task tv_get_status_keeps_the_fields_that_worked_when_one_sub_read_is_denied(string wireCode)
    {
        var connection = HealthyTv();
        connection.Fail(ForegroundAppUri, FailureFor(wireCode));
        var harness = new TestHarness(connection);

        var result = await ToolsFor(harness).GetStatus(CancellationToken.None);

        Assert.True(result.Ok);

        var json = Json(result);
        Assert.Equal("Active", json.GetProperty("power").GetString());
        Assert.Equal(12, json.GetProperty("volume").GetProperty("volume").GetInt32());

        // Present and null, not absent: a caller can tell "denied" from "not part
        // of this response".
        Assert.Equal(JsonValueKind.Null, json.GetProperty("foregroundApp").ValueKind);

        var warning = Assert.Single(WarningsOf(result));
        Assert.Equal("foregroundApp", warning.Field);
        Assert.Equal(wireCode, warning.Code);
        Assert.NotEmpty(warning.Message);
    }

    [Theory]
    [InlineData("TV_PERMISSION_DENIED")]
    [InlineData("TV_UNSUPPORTED_CAPABILITY")]
    [InlineData("TV_ERROR")]
    public async Task tv_get_device_info_returns_system_info_when_software_info_is_denied(string wireCode)
    {
        // The observed case: getCurrentSWInformation is refused while
        // system/getSystemInfo succeeds, and the denied read ran first and hid it.
        var connection = HealthyTv();
        connection.Fail(SoftwareInfoUri, FailureFor(wireCode));
        var harness = new TestHarness(connection);

        var result = await ToolsFor(harness).GetDeviceInfo(CancellationToken.None);

        Assert.True(result.Ok);

        var json = Json(result);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("software").ValueKind);
        Assert.Equal("SYNTHETIC", json.GetProperty("system").GetProperty("modelName").GetString());

        var warning = Assert.Single(WarningsOf(result));
        Assert.Equal("software", warning.Field);
        Assert.Equal(wireCode, warning.Code);
    }

    [Fact]
    public async Task An_all_success_response_carries_no_warnings_field_at_all()
    {
        // Byte-identical to the pre-change response for existing callers. An empty
        // array would be a new field where there was none.
        var harness = new TestHarness(HealthyTv());

        var status = Json(await ToolsFor(harness).GetStatus(CancellationToken.None));
        Assert.False(status.TryGetProperty("warnings", out _));
        Assert.Equal(
            ["power", "foregroundApp", "volume"],
            status.EnumerateObject().Select(p => p.Name));

        var info = Json(await ToolsFor(new TestHarness(HealthyTv())).GetDeviceInfo(CancellationToken.None));
        Assert.False(info.TryGetProperty("warnings", out _));
        Assert.Equal(["software", "system"], info.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Every_denied_sub_read_gets_its_own_warning()
    {
        var connection = HealthyTv();
        connection.Fail(ForegroundAppUri, TvException.PermissionDenied("401 insufficient permissions"));
        connection.Fail(VolumeUri, TvException.Unsupported("volume"));
        var harness = new TestHarness(connection);

        var result = await ToolsFor(harness).GetStatus(CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("Active", Json(result).GetProperty("power").GetString());
        Assert.Equal(
            ["foregroundApp", "volume"],
            WarningsOf(result).Select(w => w.Field));
    }

    [Fact]
    public void Only_command_level_codes_may_degrade_a_field()
    {
        // The allowlist is the guard. A code added later must be considered
        // explicitly rather than inheriting partial-result behaviour by default.
        Assert.True(PartialRead.IsCommandLevel(TvErrorCode.TvPermissionDenied));
        Assert.True(PartialRead.IsCommandLevel(TvErrorCode.TvUnsupportedCapability));
        Assert.True(PartialRead.IsCommandLevel(TvErrorCode.TvError));

        foreach (var code in new[]
        {
            TvErrorCode.PairingRequired,
            TvErrorCode.TvOff,
            TvErrorCode.TvUnreachable,
            TvErrorCode.Timeout,
            TvErrorCode.PairingDisabled,
            TvErrorCode.PairingDenied,
            TvErrorCode.PairingTimeout,
            TvErrorCode.KeyStorageReadOnly,
            TvErrorCode.InvalidInput,
        })
        {
            Assert.False(PartialRead.IsCommandLevel(code), $"{code} must not degrade a field.");
        }
    }

    // -------------------------------- aggregate: connection-level NEGATIVE control

    public static TheoryData<string, TvErrorCode> SessionLevelFailures => new()
    {
        { "tv_off", TvErrorCode.TvOff },
        { "unreachable", TvErrorCode.TvUnreachable },
        { "unpaired", TvErrorCode.PairingRequired },
        { "timeout", TvErrorCode.Timeout },
    };

    [Theory]
    [MemberData(nameof(SessionLevelFailures))]
    public async Task A_connection_level_failure_fails_the_whole_status_call(string kind, TvErrorCode expected)
    {
        var harness = HarnessFailingAtSessionLevel(kind);

        var result = await ToolsFor(harness).GetStatus(CancellationToken.None);

        AssertWholeCallFailed(result, expected);
    }

    [Theory]
    [MemberData(nameof(SessionLevelFailures))]
    public async Task A_connection_level_failure_fails_the_whole_device_info_call(string kind, TvErrorCode expected)
    {
        var harness = HarnessFailingAtSessionLevel(kind);

        var result = await ToolsFor(harness).GetDeviceInfo(CancellationToken.None);

        AssertWholeCallFailed(result, expected);
    }

    private static void AssertWholeCallFailed(ToolResult result, TvErrorCode expected)
    {
        // The specific lie this rules out: ok:true with every field null for a TV
        // that was off, unreachable or never paired. A caller checking ok must
        // never be told a call succeeded when nothing was read.
        Assert.False(result.Ok);
        Assert.Equal(expected.ToWireCode(), result.Error!.Code);
        Assert.Null(result.Result);
    }

    private static TestHarness HarnessFailingAtSessionLevel(string kind)
    {
        var connection = HealthyTv();

        switch (kind)
        {
            case "tv_off":
                connection.ConnectFailure = TvException.Off();
                break;
            case "unreachable":
                connection.ConnectFailure = TvException.Unreachable("no route to host");
                break;
            case "timeout":
                // A sub-read that times out mid-call, rather than at connect: even
                // then the remaining reads are not attempted and no envelope is
                // synthesised.
                connection.Fail(PowerStateUri, TvException.TimedOut("get_power_state"));
                connection.Fail(SoftwareInfoUri, TvException.TimedOut("get_software_info"));
                break;
        }

        var harness = new TestHarness(connection);

        if (kind == "unpaired")
        {
            harness.KeyStore.Current = null;
        }

        return harness;
    }

    [Fact]
    public async Task A_connection_level_failure_on_the_first_sub_read_stops_the_rest()
    {
        var connection = HealthyTv();
        connection.Fail(PowerStateUri, TvException.Off());
        var harness = new TestHarness(connection);

        var result = await ToolsFor(harness).GetStatus(CancellationToken.None);

        Assert.False(result.Ok);

        // Only the first read was attempted. Continuing would put avoidable
        // traffic on a TV that has already said it cannot serve this call.
        Assert.Equal([PowerStateUri], connection.RequestUris);
    }

    // -------------------------------------------------- the tool that reported it

    [Fact]
    public async Task A_denied_close_app_reports_the_real_reason_on_a_registered_session()
    {
        // The exact symptom from the report: tv_close_app answered "No valid client
        // key" while the session was registered and an adjacent SSAP call had just
        // succeeded. The assertion below is deliberately paired — the right code
        // AND evidence the session was healthy at the time.
        const string CloseAppUri = "ssap://system.launcher/close";

        var connection = HealthyTv();
        connection.Fail(CloseAppUri, TvException.PermissionDenied("403 access denied"));
        var harness = new TestHarness(connection);

        // An ordinary command first, so "the session works" is demonstrated rather
        // than assumed.
        Assert.Equal(PowerState.Active, await harness.Control.GetPowerStateAsync(CancellationToken.None));

        var ex = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.CloseAppAsync("com.example.synthetic", CancellationToken.None));

        Assert.Equal(TvErrorCode.TvPermissionDenied, ex.Code);
        Assert.DoesNotContain("No valid client key", ex.Message, StringComparison.Ordinal);

        // Still one registration, still the same key: nothing about a denied
        // command disturbed the pairing.
        Assert.Equal(1, connection.RegisterCount);
        Assert.Equal("test-client-key", harness.KeyStore.Current);
    }

    // ------------------------------------------------------ the real wire shape

    [Fact]
    public async Task The_partial_result_shape_survives_a_real_tools_call()
    {
        // In-process assertions cannot see this. The SDK serialises with
        // DefaultIgnoreCondition = WhenWritingNull, so a denied field would be
        // DROPPED rather than null unless it is explicitly forced — and every
        // in-process check would still pass while the wire contract was broken.
        var connection = HealthyTv();
        connection.Fail(ForegroundAppUri, TvException.PermissionDenied("401 insufficient permissions"));

        await using var fixture = await StdioFixture.StartAsync(connection);

        var result = await fixture.Client.CallToolAsync(
            "tv_get_status", cancellationToken: CancellationToken.None);

        Assert.NotEqual(true, result.IsError);

        using var document = JsonDocument.Parse(
            string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text)));

        var payload = document.RootElement.GetProperty("result");

        Assert.Equal("Active", payload.GetProperty("power").GetString());
        Assert.Equal(12, payload.GetProperty("volume").GetProperty("volume").GetInt32());

        // Present AND null on the wire, not omitted.
        Assert.True(payload.TryGetProperty("foregroundApp", out var foreground));
        Assert.Equal(JsonValueKind.Null, foreground.ValueKind);

        var warning = Assert.Single(payload.GetProperty("warnings").EnumerateArray());
        Assert.Equal("foregroundApp", warning.GetProperty("field").GetString());
        Assert.Equal("TV_PERMISSION_DENIED", warning.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_powered_off_tv_fails_a_real_tools_call_instead_of_returning_nulls()
    {
        // The negative control at the wire, where a caller actually reads `ok`.
        var connection = HealthyTv();
        connection.ConnectFailure = TvException.Off();

        await using var fixture = await StdioFixture.StartAsync(connection);

        var result = await fixture.Client.CallToolAsync(
            "tv_get_status", cancellationToken: CancellationToken.None);

        using var document = JsonDocument.Parse(
            string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text)));

        var payload = document.RootElement;

        Assert.False(payload.GetProperty("ok").GetBoolean());
        Assert.Equal("TV_OFF", payload.GetProperty("error").GetProperty("code").GetString());
        Assert.False(payload.TryGetProperty("result", out _));
    }

    // ---------------------------------------------------------- the manifest

    [Fact]
    public void The_requested_permission_set_is_pinned()
    {
        // Pinned rather than merely inspected: a permission added here that no
        // closed SsapUri endpoint needs is scope this server has not earned, and a
        // removed one is a capability that will be denied at runtime.
        Assert.Equal(
            [
                "APP_TO_APP",
                "CLOSE",
                "CONTROL_AUDIO",
                "CONTROL_DISPLAY",
                "CONTROL_INPUT_JOYSTICK",
                "CONTROL_INPUT_MEDIA_PLAYBACK",
                "CONTROL_INPUT_MEDIA_RECORDING",
                "CONTROL_INPUT_TEXT",
                "CONTROL_INPUT_TV",
                "CONTROL_MOUSE_AND_KEYBOARD",
                "CONTROL_POWER",
                "LAUNCH",
                "LAUNCH_WEBAPP",
                "READ_APP_STATUS",
                "READ_CURRENT_CHANNEL",
                "READ_INPUT_DEVICE_LIST",
                "READ_INSTALLED_APPS",
                "READ_NETWORK_STATE",
                "READ_POWER_STATE",
                "READ_RUNNING_APPS",
                "READ_SOFTWARE_INFO",
                "READ_SYSTEM_INFO",
                "READ_TV_CHANNEL_LIST",
                "READ_UPDATE_INFO",
                "TEST_OPEN",
                "TEST_PROTECTED",
                "WRITE_NOTIFICATION_TOAST",
            ],
            SsapManifest.RequestedPermissions.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public void The_manifest_carries_no_vendor_signature_and_claims_no_lg_identity()
    {
        // Making a denied capability "work" by impersonating a signed LG app is
        // explicitly out of bounds; this is the cheap structural check that nobody
        // reached for it.
        var json = JsonSerializer.Serialize(SsapManifest.Build());

        Assert.DoesNotContain("signature", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("com.lge", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("com.anurmatov", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_permission_fingerprint_tracks_the_SET_not_its_order()
    {
        var a = SsapManifest.ComputeFingerprint(["READ_APP_STATUS", "CLOSE"]);
        var reordered = SsapManifest.ComputeFingerprint(["CLOSE", "READ_APP_STATUS"]);
        var widened = SsapManifest.ComputeFingerprint(["CLOSE", "READ_APP_STATUS", "READ_RUNNING_APPS"]);

        Assert.Equal(a, reordered);
        Assert.NotEqual(a, widened);
        Assert.Equal(16, SsapManifest.PermissionsFingerprint.Length);
    }

    // ------------------------------------------------- the one-time re-pair path

    private FileClientKeyStore KeyStoreOn(string fileName) => new(
        Options.Create(new WebosMcpOptions
        {
            Host = "192.0.2.10",
            ClientKeyPath = Path.Combine(_root, fileName),
        }),
        NullLoggerFactory.Instance.CreateLogger<FileClientKeyStore>());

    [Fact]
    public async Task A_key_persisted_now_records_the_permission_set_it_was_granted_under()
    {
        Directory.CreateDirectory(_root);
        var store = KeyStoreOn("fresh.json");

        await store.PersistAsync("synthetic-key", CancellationToken.None);

        Assert.False(await store.IsGrantStaleAsync(CancellationToken.None));

        var stored = JsonSerializer.Deserialize<JsonElement>(
            await File.ReadAllTextAsync(Path.Combine(_root, "fresh.json")));

        Assert.Equal(
            SsapManifest.PermissionsFingerprint,
            stored.GetProperty("permissionsFingerprint").GetString());
    }

    [Theory]
    [InlineData("""{"clientKey":"synthetic-key"}""")]
    [InlineData("""{"clientKey":"synthetic-key","permissionsFingerprint":"0000000000000000"}""")]
    public async Task A_key_granted_under_an_older_permission_set_reads_as_stale(string onDisk)
    {
        // The first case is every key paired by an earlier build: no fingerprint at
        // all, which is exactly when a newly added permission will be denied.
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "old.json"), onDisk);

        var store = KeyStoreOn("old.json");

        Assert.True(await store.IsGrantStaleAsync(CancellationToken.None));

        // Read-only in every sense: the key still works and the file is untouched.
        Assert.Equal("synthetic-key", await store.ReadAsync(CancellationToken.None));
        Assert.Equal(onDisk, await File.ReadAllTextAsync(Path.Combine(_root, "old.json")));
    }

    [Fact]
    public async Task A_stale_grant_adds_the_explicit_re_pair_hint_without_changing_the_code()
    {
        var connection = HealthyTv();
        connection.Fail(ForegroundAppUri, TvException.PermissionDenied("401 insufficient permissions"));
        var harness = new TestHarness(connection);
        harness.KeyStore.GrantIsStale = true;

        var ex = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.GetForegroundAppAsync(CancellationToken.None));

        Assert.Equal(TvErrorCode.TvPermissionDenied, ex.Code);
        Assert.Contains("re-pair explicitly", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing re-pairs on its own", ex.Message, StringComparison.Ordinal);

        // The hint is a hint. Nothing was written, re-paired, or cleared.
        Assert.Empty(harness.KeyStore.Persists);
        Assert.Empty(harness.KeyStore.Writes);
        Assert.Equal("test-client-key", harness.KeyStore.Current);
    }

    [Fact]
    public async Task A_current_grant_gets_no_re_pair_hint()
    {
        // The negative control: telling someone to re-pair when re-pairing cannot
        // help is its own kind of dishonest.
        var connection = HealthyTv();
        connection.Fail(ForegroundAppUri, TvException.PermissionDenied("401 insufficient permissions"));
        var harness = new TestHarness(connection);
        harness.KeyStore.GrantIsStale = false;

        var ex = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.GetForegroundAppAsync(CancellationToken.None));

        Assert.Equal(TvErrorCode.TvPermissionDenied, ex.Code);
        Assert.DoesNotContain("re-pair explicitly", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stale_grant_never_triggers_pairing_on_an_ordinary_command()
    {
        // The constraint this protects: a manifest change must never fire a prompt
        // at someone who did not ask for one.
        var harness = new TestHarness(HealthyTv());
        harness.KeyStore.GrantIsStale = true;

        await harness.Control.GetPowerStateAsync(CancellationToken.None);

        Assert.Empty(harness.KeyStore.Persists);
        Assert.Equal(1, harness.Connection.RegisterCount);
        Assert.Equal("test-client-key", harness.Connection.LastClientKey);
    }

    private static TvException FailureFor(string wireCode) => wireCode switch
    {
        "TV_PERMISSION_DENIED" => TvException.PermissionDenied("401 insufficient permissions"),
        "TV_UNSUPPORTED_CAPABILITY" => TvException.Unsupported("the requested read"),
        "TV_ERROR" => new TvException(TvErrorCode.TvError, "The TV reported: something went wrong"),
        _ => throw new ArgumentOutOfRangeException(nameof(wireCode), wireCode, null),
    };
}

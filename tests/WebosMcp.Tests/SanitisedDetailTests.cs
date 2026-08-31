using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using WebosMcp.Application;
using WebosMcp.Domain;
using WebosMcp.Infrastructure;
using WebosMcp.Server.Hosting;
using WebosMcp.Server.Tools;
using WebosMcp.Tests.Fakes;
using Xunit;

namespace WebosMcp.Tests;

/// <summary>
/// Two things that leave this process carrying data it did not author: the TV's
/// own error wording, and the operator's device book.
///
/// Both reach places where raw content is a problem — a caller's response, and
/// every log sink the deployment has. The TV's wording arrives over the network
/// and cannot be assumed well behaved; the device identifier is nobody's business
/// but the operator's.
/// </summary>
public sealed class SanitisedDetailTests
{
    /// <summary>
    /// A refusal message shaped like an attack: log-forging newlines, an ANSI
    /// escape sequence, a NUL, and enough padding to bury whatever follows it.
    /// </summary>
    private const string HostileDetail =
        "403 access denied\r\nFATAL Anything can be forged here\u001B[31m\0 " +
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" +
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" +
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" +
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static void AssertNeutralised(string text)
    {
        Assert.DoesNotContain('\n', text);
        Assert.DoesNotContain('\r', text);
        Assert.DoesNotContain('\u001B', text);
        Assert.DoesNotContain('\0', text);
        Assert.DoesNotContain(text, c => char.IsControl(c));
    }

    // --------------------------------------------------------- the sanitiser

    [Fact]
    public void Ordinary_tv_wording_passes_through_unchanged()
    {
        // The point of keeping the detail at all is naming which capability was
        // refused. A sanitiser that mangled normal text would defeat that.
        Assert.Equal("401 insufficient permissions", TvException.SanitizeDetail("401 insufficient permissions"));
        Assert.Equal("403 access denied", TvException.SanitizeDetail("403 access denied"));
        Assert.Equal("Permission denied for app com.example", TvException.SanitizeDetail("Permission denied for app com.example"));
    }

    [Theory]
    [InlineData("a\nb", "a b")]
    [InlineData("a\r\nb", "a b")]
    [InlineData("a\tb", "a b")]
    [InlineData("a\0b", "a b")]
    [InlineData("a\u001B[31mb", "a [31mb")]
    [InlineData("  padded  ", "padded")]
    [InlineData("a \n\t b", "a b")]
    public void Control_characters_become_a_single_space(string input, string expected)
    {
        Assert.Equal(expected, TvException.SanitizeDetail(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\0\u0001\u0002")]
    public void Detail_that_carries_nothing_usable_says_so(string? input)
    {
        Assert.Equal("(no detail)", TvException.SanitizeDetail(input));
    }

    [Fact]
    public void Over_long_detail_is_capped()
    {
        var sanitised = TvException.SanitizeDetail(new string('x', 10_000));

        Assert.True(
            sanitised.Length <= TvException.MaxDetailLength + 1,
            $"Detail was {sanitised.Length} characters; the cap is {TvException.MaxDetailLength}.");

        Assert.EndsWith("…", sanitised, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_path_that_carries_tv_wording_sanitises_it()
    {
        // Both factories, and both mappers that build on them. Missing one would
        // leave a raw path open through a different door.
        AssertNeutralised(TvException.PermissionDenied(HostileDetail).Message);
        AssertNeutralised(TvException.Reported(TvErrorCode.TvError, HostileDetail).Message);
        AssertNeutralised(SsapWebSocketConnection.MapRequestError(HostileDetail).Message);
        AssertNeutralised(SsapWebSocketConnection.MapRegistrationError(HostileDetail).Message);
        AssertNeutralised(SsapWebSocketConnection.MapRequestError(HostileDetail + " not supported").Message);
    }

    [Fact]
    public void Classification_still_reads_the_raw_text()
    {
        // Sanitising before classifying would let a control character hide the
        // word it sits inside, and a denial would be misreported as a generic
        // error. Raw in, sanitised out.
        Assert.Equal(
            TvErrorCode.TvPermissionDenied,
            SsapWebSocketConnection.MapRequestError("40\03 access\u001Bdenied").Code);

        Assert.Equal(
            TvErrorCode.TvUnsupportedCapability,
            SsapWebSocketConnection.MapRequestError("this is\nnot supported").Code);
    }

    // ------------------------------------------------ end to end, wire and logs

    [Fact]
    public async Task Hostile_tv_wording_is_neutralised_in_the_response_and_in_the_logs()
    {
        // The two places it actually lands. ToolInvoker logs the exception message
        // at Information, so an unsanitised message forges log entries in whatever
        // sink the deployment uses — a failure nobody sees by reading the response.
        var capture = new CapturingLoggerProvider();

        var connection = new FakeSsapConnection();
        connection.Respond(
            "ssap://com.webos.service.tvpower/power/getPowerState",
            """{"returnValue":true,"state":"Active"}""");

        // The exact object the production mapper builds from this text.
        connection.Fail(
            "ssap://com.webos.applicationManager/getForegroundAppInfo",
            SsapWebSocketConnection.MapRequestError(HostileDetail));

        await using var fixture = await StdioFixture.StartAsync(connection, loggerProvider: capture);

        var result = await fixture.Client.CallToolAsync(
            "tv_get_foreground_app", cancellationToken: CancellationToken.None);

        var body = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        using var document = JsonDocument.Parse(body);
        var error = document.RootElement.GetProperty("error");

        Assert.Equal("TV_PERMISSION_DENIED", error.GetProperty("code").GetString());

        var message = error.GetProperty("message").GetString()!;
        AssertNeutralised(message);

        // The useful part survives: a caller can still see what was refused.
        Assert.Contains("403 access denied", message, StringComparison.Ordinal);

        // The tool did log the denial — otherwise the assertions below would pass
        // on an empty log.
        Assert.Contains(capture.Lines, line => line.Contains("TV_PERMISSION_DENIED", StringComparison.Ordinal));

        // One log event stays ONE line. That is the property that matters: the
        // sanitiser neutralises structure, it does not censor content, so the
        // attacker's words may well still appear — they simply cannot become a
        // separate entry, move a cursor, or truncate a consumer.
        Assert.All(capture.Lines, line =>
        {
            Assert.DoesNotContain('\n', line);
            Assert.DoesNotContain('\r', line);
            Assert.DoesNotContain('\u001B', line);
            Assert.DoesNotContain('\0', line);
        });

        // Specifically: the forged "FATAL ..." text cannot begin a line of its own.
        Assert.DoesNotContain(
            capture.Lines,
            line => line.TrimStart().StartsWith("FATAL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_denied_sub_read_warning_carries_sanitised_wording_too()
    {
        // The partial-result path is a third destination for the same text.
        var connection = new FakeSsapConnection();
        connection.Respond(
            "ssap://com.webos.service.tvpower/power/getPowerState",
            """{"returnValue":true,"state":"Active"}""");
        connection.Respond("ssap://audio/getVolume", """{"returnValue":true,"volume":12,"muted":false}""");
        connection.Fail(
            "ssap://com.webos.applicationManager/getForegroundAppInfo",
            SsapWebSocketConnection.MapRequestError(HostileDetail));

        var harness = new TestHarness(connection);
        var tools = new StatusTools(harness.Control, NullLogger<StatusTools>.Instance);

        var json = JsonSerializer.SerializeToElement(
            (await tools.GetStatus(CancellationToken.None)).Result,
            ModelContextProtocol.McpJsonUtilities.DefaultOptions);

        var warning = Assert.Single(json.GetProperty("warnings").EnumerateArray());

        AssertNeutralised(warning.GetProperty("message").GetString()!);
    }

    // ------------------------------------------------- the device book at startup

    private sealed class StubDeviceStore(DeviceBook book) : IDeviceStore
    {
        public Task<DeviceBook> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(book);

        public Task SaveAsync(DeviceBook updated, CancellationToken cancellationToken) => Task.CompletedTask;

        public string DescribeLocation() => "(in memory)";
    }

    private sealed class StubDiscovery : ITvDiscovery
    {
        public Task<IReadOnlyList<DiscoveredTv>> DiscoverAsync(TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DiscoveredTv>>([]);
    }

    private sealed class StubNetworkFacts : INetworkFacts
    {
        public string? TryGetMacAddress(string host) => null;

        public string? TryGetBroadcastAddress(string host) => null;

        public Task<bool> IsReachableAsync(string host, int port, CancellationToken ct) => Task.FromResult(true);
    }

    [Fact]
    public async Task Startup_does_not_log_the_stored_device_identifier()
    {
        // Applying a stored selection is worth one log line; WHICH device it was is
        // not, and writing an identifier into every sink for the process lifetime
        // answers a question nobody asked. tv_list_devices exists for that.
        const string DeviceId = "device-identifier-that-must-not-be-logged";
        const string Host = "192.0.2.10";
        const string FriendlyName = "friendly-name-that-must-not-be-logged";

        var book = new DeviceBook(
            [new TvDevice(DeviceId, Host, "00:11:22:33:44:55", "192.0.2.255", FriendlyName, "MODEL-X")],
            DeviceId);

        var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(capture);
        });

        var devices = new DeviceService(
            new StubDeviceStore(book),
            new StubDiscovery(),
            new StubNetworkFacts(),
            Options.Create(new WebosMcpOptions()));

        var applier = new ActiveDeviceApplier(devices, factory.CreateLogger<ActiveDeviceApplier>());

        await applier.StartAsync(CancellationToken.None);

        var logs = string.Join("\n", capture.Lines);

        // It ran — otherwise the assertions below would pass on an empty log.
        Assert.Contains(
            capture.Lines,
            line => line.Contains("active TV selection", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(DeviceId, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(FriendlyName, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(Host, logs, StringComparison.Ordinal);
        Assert.DoesNotContain("00:11:22:33:44:55", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("MODEL-X", logs, StringComparison.Ordinal);
    }
}

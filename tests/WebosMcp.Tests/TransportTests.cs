using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WebosMcp.Application;
using WebosMcp.Server.Configuration;
using WebosMcp.Server.Hosting;
using WebosMcp.Tests.Fakes;
using Xunit;

namespace WebosMcp.Tests;

/// <summary>
/// Both transports end to end, over the real MCP protocol, against the same
/// shared tool layer. Fakes stand in for the TV only.
/// </summary>
public sealed class TransportTests
{
    private static readonly string[] ExpectedToolNames =
    [
        "tv_get_power_state",
        "tv_get_status",
        "tv_power_on",
        "tv_power_off",
        "tv_set_volume",
        "tv_launch_app",
        "tv_open_url",
        "tv_youtube_search",
        "tv_send_button",
        "tv_list_inputs",
        "tv_show_toast",
        "tv_take_screenshot",
    ];

    internal static void ReplaceTvWithFake(
        IServiceCollection services,
        FakeSsapConnection connection,
        FakeScreenshotDownloader? downloader = null)
    {
        services.RemoveAll<ISsapConnectionFactory>();
        services.AddSingleton<ISsapConnectionFactory>(new FakeSsapConnectionFactory().Enqueue(connection));

        services.RemoveAll<IClientKeyStore>();
        services.AddSingleton<IClientKeyStore>(new FakeClientKeyStore());

        services.RemoveAll<IWolSender>();
        services.AddSingleton<IWolSender>(new FakeWolSender());

        services.RemoveAll<IDelayProvider>();
        services.AddSingleton<IDelayProvider>(new InstantDelayProvider());

        services.RemoveAll<IDialClient>();
        services.AddSingleton<IDialClient>(new FakeDialClient());

        // Replaced unconditionally, not only for screenshot tests: leaving the real
        // downloader registered would let a future test reach the network.
        services.RemoveAll<IScreenshotDownloader>();
        services.AddSingleton<IScreenshotDownloader>(downloader ?? new FakeScreenshotDownloader());

        services.Configure<WebosMcpOptions>(options =>
        {
            options.Host = "192.0.2.10";
            options.MacAddress = "00:11:22:33:44:55";
            options.FallbackStepDelayMilliseconds = 0;
        });
    }

    // ------------------------------------------------------------ stdio

    [Fact]
    public async Task Stdio_transport_lists_and_invokes_the_shared_tool_layer()
    {
        var connection = new FakeSsapConnection();
        connection.Respond(
            "ssap://com.webos.service.tvpower/power/getPowerState",
            """{"returnValue":true,"state":"Active"}""");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new ConfigurationBuilder().Build());
        services.AddWebosMcp(new ConfigurationBuilder().Build());
        ReplaceTvWithFake(services, connection);

        services
            .AddMcpServer(options => options.ServerInfo = new() { Name = "webos-mcp", Version = "1.0.0" })
            .WithToolsFromAssembly(typeof(HttpServerHost).Assembly);

        await using var provider = services.BuildServiceProvider();

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        await using var serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream(),
            "webos-mcp",
            NullLoggerFactory.Instance);

        var serverOptions = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        await using var server = McpServer.Create(
            serverTransport, serverOptions, NullLoggerFactory.Instance, provider);

        var serverTask = server.RunAsync(CancellationToken.None);

        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(),
            serverToClient.Reader.AsStream(),
            NullLoggerFactory.Instance);

        await using var client = await McpClient.CreateAsync(
            clientTransport, cancellationToken: CancellationToken.None);

        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var names = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var expected in ExpectedToolNames)
        {
            Assert.Contains(expected, names);
        }

        var result = await client.CallToolAsync(
            "tv_get_power_state",
            cancellationToken: CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        var payload = JsonDocument.Parse(GetText(result));
        Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("Active", payload.RootElement.GetProperty("result").GetProperty("state").GetString());

        await client.DisposeAsync();
        await serverTransport.DisposeAsync();
        await Task.WhenAny(serverTask, Task.Delay(2000, CancellationToken.None));
    }

    [Fact]
    public async Task Stdio_transport_surfaces_the_error_contract_as_structured_data()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWebosMcp(new ConfigurationBuilder().Build());
        ReplaceTvWithFake(services, new FakeSsapConnection());

        // No client key: every tool must return PAIRING_REQUIRED.
        services.RemoveAll<IClientKeyStore>();
        services.AddSingleton<IClientKeyStore>(new FakeClientKeyStore(null));

        services
            .AddMcpServer(options => options.ServerInfo = new() { Name = "webos-mcp", Version = "1.0.0" })
            .WithToolsFromAssembly(typeof(HttpServerHost).Assembly);

        await using var provider = services.BuildServiceProvider();

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        await using var serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream(),
            "webos-mcp",
            NullLoggerFactory.Instance);

        var serverOptions = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        await using var server = McpServer.Create(
            serverTransport, serverOptions, NullLoggerFactory.Instance, provider);

        var serverTask = server.RunAsync(CancellationToken.None);

        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(),
            serverToClient.Reader.AsStream(),
            NullLoggerFactory.Instance);

        await using var client = await McpClient.CreateAsync(
            clientTransport, cancellationToken: CancellationToken.None);

        var result = await client.CallToolAsync(
            "tv_get_power_state",
            cancellationToken: CancellationToken.None);

        var payload = JsonDocument.Parse(GetText(result));
        Assert.False(payload.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "PAIRING_REQUIRED",
            payload.RootElement.GetProperty("error").GetProperty("code").GetString());

        await client.DisposeAsync();
        await serverTransport.DisposeAsync();
        await Task.WhenAny(serverTask, Task.Delay(2000, CancellationToken.None));
    }

    // ------------------------------------------------------------- http

    private static WebApplication BuildHttpApp(
        HttpTransportSettings settings,
        FakeSsapConnection connection,
        FakeScreenshotDownloader? downloader = null) =>
        HttpServerHost.Build(settings, [], builder =>
        {
            builder.WebHost.UseTestServer();
            ReplaceTvWithFake(builder.Services, connection, downloader);
        });

    [Fact]
    public async Task Http_transport_rejects_an_unauthenticated_request_with_401()
    {
        var settings = new HttpTransportSettings
        {
            BindAddress = "0.0.0.0",
            Port = 8765,
            Token = "s3cret",
        };

        await using var app = BuildHttpApp(settings, new FakeSsapConnection());
        await app.StartAsync(CancellationToken.None);

        using var client = app.GetTestClient();
        using var response = await client.PostAsync(
            "/", new StringContent("{}"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await app.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Http_transport_rejects_a_wrong_token_with_401()
    {
        var settings = new HttpTransportSettings
        {
            BindAddress = "0.0.0.0",
            Port = 8765,
            Token = "s3cret",
        };

        await using var app = BuildHttpApp(settings, new FakeSsapConnection());
        await app.StartAsync(CancellationToken.None);

        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");

        using var response = await client.PostAsync(
            "/", new StringContent("{}"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await app.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Http_transport_serves_the_same_tool_layer_when_authenticated()
    {
        var connection = new FakeSsapConnection();
        connection.Respond(
            "ssap://com.webos.service.tvpower/power/getPowerState",
            """{"returnValue":true,"state":"Active"}""");

        var settings = new HttpTransportSettings
        {
            BindAddress = "0.0.0.0",
            Port = 8765,
            Token = "s3cret",
        };

        await using var app = BuildHttpApp(settings, connection);
        await app.StartAsync(CancellationToken.None);

        var httpClient = app.GetTestClient();
        httpClient.BaseAddress = new Uri("http://localhost/");
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "s3cret");

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/"),
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            httpClient,
            NullLoggerFactory.Instance,
            ownsHttpClient: true);

        await using var client = await McpClient.CreateAsync(
            transport, cancellationToken: CancellationToken.None);

        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var names = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var expected in ExpectedToolNames)
        {
            Assert.Contains(expected, names);
        }

        var result = await client.CallToolAsync(
            "tv_get_power_state",
            cancellationToken: CancellationToken.None);

        var payload = JsonDocument.Parse(GetText(result));
        Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("Active", payload.RootElement.GetProperty("result").GetProperty("state").GetString());

        await app.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_loopback_bind_with_no_token_serves_without_auth()
    {
        var settings = new HttpTransportSettings
        {
            BindAddress = "127.0.0.1",
            Port = 8765,
            Token = null,
        };

        await using var app = BuildHttpApp(settings, new FakeSsapConnection());
        await app.StartAsync(CancellationToken.None);

        using var client = app.GetTestClient();
        using var response = await client.PostAsync(
            "/", new StringContent("{}"), CancellationToken.None);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);

        await app.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task No_tool_exposes_a_raw_command_surface()
    {
        var names = RegisteredToolNames(enablePairing: false);
        Assert.NotEmpty(names);

        // "pair" is no longer in this list: pairing is an approved, opt-in
        // surface. Its absence by default is asserted separately below, so the
        // two boundaries stay independently verifiable.
        string[] forbidden = ["ssap", "raw", "command", "exec"];
        foreach (var name in names)
        {
            foreach (var word in forbidden)
            {
                Assert.False(
                    name.Contains(word, StringComparison.OrdinalIgnoreCase),
                    $"Tool '{name}' looks like a forbidden surface (matched '{word}').");
            }
        }

        await Task.CompletedTask;
    }

    [Fact]
    public void Frame_capture_is_exactly_one_approved_read_only_tool()
    {
        // Capture used to be prohibited outright and is now a single, named,
        // read-only tool. The boundary did not disappear — it narrowed, so it is
        // asserted as an allowlist: any SECOND capture-shaped tool (recording,
        // polling, a frame grabber taking a URI) fails this.
        string[] capture = ["screenshot", "capture", "record", "frame"];

        var matches = RegisteredToolNames(enablePairing: true)
            .Where(name => capture.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.Equal(["tv_take_screenshot"], matches);
    }

    [Fact]
    public async Task The_screenshot_tool_takes_no_arguments_at_all()
    {
        // The real boundary is not the tool's name: it is that no caller can
        // influence what is requested. An empty schema is what makes the capture
        // URI unreachable from outside — a single added parameter would reopen it.
        await using var fixture = await StdioFixture.StartAsync(new FakeSsapConnection());

        var tool = (await fixture.Client.ListToolsAsync(cancellationToken: CancellationToken.None))
            .Single(t => t.Name == "tv_take_screenshot");

        var schema = JsonSerializer.SerializeToElement(tool.ProtocolTool.InputSchema);

        var properties = schema.TryGetProperty("properties", out var declared)
            ? declared.EnumerateObject().Select(p => p.Name).ToArray()
            : [];

        Assert.Empty(properties);
    }

    [Fact]
    public void Pair_device_is_absent_by_default()
    {
        // Default deployments keep the original no-pairing-surface boundary:
        // the tool is not registered at all, so it cannot be listed or called.
        Assert.DoesNotContain("pair_device", RegisteredToolNames(enablePairing: false));
    }

    [Fact]
    public void Pair_device_appears_only_when_explicitly_opted_in()
    {
        Assert.Contains("pair_device", RegisteredToolNames(enablePairing: true));
    }

    [Fact]
    public async Task Pair_device_is_not_listed_over_stdio_by_default()
    {
        await using var fixture = await StdioFixture.StartAsync(new FakeSsapConnection());

        var tools = await fixture.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        Assert.DoesNotContain("pair_device", tools.Select(t => t.Name));

        // The rest of the surface is unaffected by the gate.
        Assert.Contains("tv_get_power_state", tools.Select(t => t.Name));
    }

    private static IReadOnlyList<string> RegisteredToolNames(bool enablePairing)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WebosMcp:EnablePairingTool"] = enablePairing ? "true" : "false",
            })
            .Build();

        services.AddWebosMcp(configuration);
        ReplaceTvWithFake(services, new FakeSsapConnection());

        services
            .AddMcpServer(options => options.ServerInfo = new() { Name = "webos-mcp", Version = "1.0.0" })
            .AddWebosMcpTools(configuration);

        using var provider = services.BuildServiceProvider();
        return [.. provider.GetServices<McpServerTool>().Select(t => t.ProtocolTool.Name)];
    }

    private static string GetText(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    /// <summary>
    /// Drives a real Streamable HTTP tools/call and hands back the image block.
    /// </summary>
    private static async Task<ImageContentBlock> CaptureOverHttpAsync(byte[] body)
    {
        var connection = new FakeSsapConnection();
        connection.Respond(
            "ssap://tv/executeOneShot",
            """{"returnValue":true,"imageUri":"http://192.0.2.10:9080/tmp/capture.jpg"}""");

        var settings = new HttpTransportSettings
        {
            BindAddress = "0.0.0.0",
            Port = 8765,
            Token = "s3cret",
        };

        await using var app = BuildHttpApp(
            settings, connection, new FakeScreenshotDownloader { Body = body });

        await app.StartAsync(CancellationToken.None);

        var httpClient = app.GetTestClient();
        httpClient.BaseAddress = new Uri("http://localhost/");
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "s3cret");

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/"),
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            httpClient,
            NullLoggerFactory.Instance,
            ownsHttpClient: true);

        await using var client = await McpClient.CreateAsync(
            transport, cancellationToken: CancellationToken.None);

        var result = await client.CallToolAsync(
            "tv_take_screenshot", cancellationToken: CancellationToken.None);

        Assert.NotEqual(true, result.IsError);

        var block = Assert.IsType<ImageContentBlock>(Assert.Single(result.Content));

        // No text or base64-string content block alongside it.
        Assert.Empty(result.Content.OfType<TextContentBlock>());

        await app.StopAsync(CancellationToken.None);

        return block;
    }

    [Fact]
    public async Task Http_transport_returns_a_png_that_really_decodes_to_the_expected_image()
    {
        // The success criterion is a DECODE, not a byte-compare against the
        // fixture. Comparing bytes proves the transport copied an array; it says
        // nothing about whether that array was ever a usable image, and would pass
        // identically if the fixture were garbage.
        var block = await CaptureOverHttpAsync(ImageFixtures.Png);

        Assert.Equal("image/png", block.MimeType);

        // Decoded from the wire bytes: chunk walk, zlib inflate, unfilter, pixels.
        var image = ImageDecoding.DecodePng(block.DecodedData.ToArray());

        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(2 * 2 * 4, image.Rgba.Length);

        // Every pixel is the fixture's solid colour, alpha opaque.
        for (var pixel = 0; pixel < 4; pixel++)
        {
            Assert.Equal(
                new byte[] { 0x20, 0x60, 0xA0, 0xFF },
                image.Rgba[(pixel * 4)..((pixel * 4) + 4)]);
        }
    }

    [Fact]
    public async Task Http_transport_returns_a_jpeg_whose_frame_header_reports_the_expected_size()
    {
        // JPEG is what the TV actually returns, so it gets its own end-to-end pass.
        // Reaching a well-formed frame header requires every preceding marker
        // segment to be intact.
        var block = await CaptureOverHttpAsync(ImageFixtures.Jpeg);

        Assert.Equal("image/jpeg", block.MimeType);

        var (width, height) = ImageDecoding.ReadJpegDimensions(block.DecodedData.ToArray());

        Assert.Equal(2, width);
        Assert.Equal(2, height);
    }
}

/// <summary>
/// Boots a real MCP server over an in-memory stream pair and connects a real
/// client to it. Same shared tool layer the stdio transport serves.
/// </summary>
internal sealed class StdioFixture : IAsyncDisposable
{
    private readonly StreamServerTransport _serverTransport;
    private readonly McpServer _server;
    private readonly Task _serverTask;
    private readonly ServiceProvider _provider;

    private StdioFixture(
        ServiceProvider provider,
        StreamServerTransport serverTransport,
        McpServer server,
        Task serverTask,
        McpClient client)
    {
        _provider = provider;
        _serverTransport = serverTransport;
        _server = server;
        _serverTask = serverTask;
        Client = client;
    }

    public McpClient Client { get; }

    public static async Task<StdioFixture> StartAsync(
        FakeSsapConnection connection,
        bool enablePairing = false,
        IClientKeyStore? keyStore = null,
        ILoggerProvider? loggerProvider = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            if (loggerProvider is not null)
            {
                b.SetMinimumLevel(LogLevel.Trace);
                b.AddProvider(loggerProvider);
            }
        });

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WebosMcp:EnablePairingTool"] = enablePairing ? "true" : "false",
            })
            .Build();

        services.AddWebosMcp(configuration);
        TransportTests.ReplaceTvWithFake(services, connection);

        if (keyStore is not null)
        {
            services.RemoveAll<IClientKeyStore>();
            services.AddSingleton(keyStore);
        }

        services
            .AddMcpServer(options => options.ServerInfo = new() { Name = "webos-mcp", Version = "1.0.0" })
            .AddWebosMcpTools(configuration);

        var provider = services.BuildServiceProvider();

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream(),
            "webos-mcp",
            NullLoggerFactory.Instance);

        var serverOptions = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var server = McpServer.Create(serverTransport, serverOptions, NullLoggerFactory.Instance, provider);
        var serverTask = server.RunAsync(CancellationToken.None);

        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(),
            serverToClient.Reader.AsStream(),
            NullLoggerFactory.Instance);

        var client = await McpClient.CreateAsync(clientTransport, cancellationToken: CancellationToken.None);

        return new StdioFixture(provider, serverTransport, server, serverTask, client);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _serverTransport.DisposeAsync();
        await Task.WhenAny(_serverTask, Task.Delay(2000));
        await _server.DisposeAsync();
        await _provider.DisposeAsync();
    }
}

using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    ];

    private static void ReplaceTvWithFake(IServiceCollection services, FakeSsapConnection connection)
    {
        services.RemoveAll<ISsapConnectionFactory>();
        services.AddSingleton<ISsapConnectionFactory>(new FakeSsapConnectionFactory().Enqueue(connection));

        services.RemoveAll<IClientKeyStore>();
        services.AddSingleton<IClientKeyStore>(new FakeClientKeyStore());

        services.RemoveAll<IWolSender>();
        services.AddSingleton<IWolSender>(new FakeWolSender());

        services.RemoveAll<IDelayProvider>();
        services.AddSingleton<IDelayProvider>(new InstantDelayProvider());

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
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddWebosMcp(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
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
        services.AddWebosMcp(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
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

    private static WebApplication BuildHttpApp(HttpTransportSettings settings, FakeSsapConnection connection) =>
        HttpServerHost.Build(settings, [], builder =>
        {
            builder.WebHost.UseTestServer();
            ReplaceTvWithFake(builder.Services, connection);
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
    public async Task No_tool_exposes_a_raw_command_screenshot_or_pairing_surface()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWebosMcp(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        ReplaceTvWithFake(services, new FakeSsapConnection());

        services
            .AddMcpServer(options => options.ServerInfo = new() { Name = "webos-mcp", Version = "1.0.0" })
            .WithToolsFromAssembly(typeof(HttpServerHost).Assembly);

        await using var provider = services.BuildServiceProvider();
        var names = provider.GetServices<McpServerTool>()
            .Select(t => t.ProtocolTool.Name)
            .ToList();

        Assert.NotEmpty(names);

        string[] forbidden = ["ssap", "raw", "command", "screenshot", "capture", "pair", "register", "exec"];
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

    private static string GetText(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}

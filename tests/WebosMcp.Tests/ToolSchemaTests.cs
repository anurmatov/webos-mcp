using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WebosMcp.Server.Hosting;
using WebosMcp.Tests.Fakes;
using Xunit;

namespace WebosMcp.Tests;

/// <summary>
/// The GENERATED MCP schema, not the C# signature.
///
/// A nullable annotation alone does not make a parameter optional in the schema —
/// it needs a default value. Physical testing hit this: a no-argument
/// tv_discover_devices call was rejected before any tool code ran, so nothing in
/// the server could have reported the problem. Only a tools/list + tools/call
/// against the real protocol can catch it.
/// </summary>
public sealed class ToolSchemaTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private McpClient _client = null!;
    private StreamServerTransport _serverTransport = null!;
    private Task _serverTask = Task.CompletedTask;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddWebosMcp(new ConfigurationBuilder().Build());
        TransportTests.ReplaceTvWithFake(services, new FakeSsapConnection());

        services
            .AddMcpServer(options => options.ServerInfo = new() { Name = "webos-mcp", Version = "1.0.0" })
            .WithToolsFromAssembly(typeof(HttpServerHost).Assembly);

        _provider = services.BuildServiceProvider();

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        _serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream(),
            "webos-mcp",
            NullLoggerFactory.Instance);

        var server = McpServer.Create(
            _serverTransport,
            _provider.GetRequiredService<IOptions<McpServerOptions>>().Value,
            NullLoggerFactory.Instance,
            _provider);

        _serverTask = server.RunAsync(CancellationToken.None);

        _client = await McpClient.CreateAsync(
            new StreamClientTransport(
                clientToServer.Writer.AsStream(),
                serverToClient.Reader.AsStream(),
                NullLoggerFactory.Instance),
            cancellationToken: CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _serverTransport.DisposeAsync();
        await Task.WhenAny(_serverTask, Task.Delay(2000));
        await _provider.DisposeAsync();
    }

    private async Task<JsonElement> SchemaFor(string toolName)
    {
        var tools = await _client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var tool = tools.Single(t => t.Name == toolName);
        return JsonSerializer.SerializeToElement(tool.ProtocolTool.InputSchema);
    }

    private static HashSet<string> Required(JsonElement schema) =>
        schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array
            ? required.EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal)
            : [];

    [Theory]
    [InlineData("tv_discover_devices", "host")]
    [InlineData("tv_register_device", "name")]
    [InlineData("tv_update_device", "macAddress")]
    [InlineData("tv_update_device", "broadcastAddress")]
    [InlineData("tv_update_device", "name")]
    public async Task Documented_optional_parameters_are_optional_in_the_generated_schema(
        string tool,
        string parameter)
    {
        var schema = await SchemaFor(tool);

        Assert.DoesNotContain(parameter, Required(schema));
    }

    [Fact]
    public async Task Genuinely_required_parameters_are_still_required()
    {
        // The fix must not make everything optional — that would trade a rejected
        // call for a call that runs with a missing address.
        Assert.Contains("host", Required(await SchemaFor("tv_register_device")));
        Assert.Contains("id", Required(await SchemaFor("tv_select_device")));
    }

    [Fact]
    public async Task tv_discover_devices_can_be_called_with_NO_arguments()
    {
        // The end-to-end proof: this is the exact invocation that was rejected.
        var result = await _client.CallToolAsync(
            "tv_discover_devices",
            cancellationToken: CancellationToken.None);

        Assert.NotEqual(true, result.IsError);

        var payload = JsonDocument.Parse(
            ((TextContentBlock)result.Content.First(c => c.Type == "text")).Text);

        Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task tv_discover_devices_still_accepts_an_address()
    {
        var result = await _client.CallToolAsync(
            "tv_discover_devices",
            new Dictionary<string, object?> { ["host"] = "192.0.2.10" },
            cancellationToken: CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
    }
}

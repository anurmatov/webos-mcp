using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace WebosMcp.Server.Hosting;

public static class StdioServerHost
{
    public static IHost Build(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // stdout is the MCP framing channel — every log line must go to stderr
        // or the transport is corrupted.
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
        builder.Logging.Services.Configure<ConsoleLoggerOptions>(
            options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddWebosMcp(builder.Configuration);

        builder.Services
            .AddMcpServer(options => options.ServerInfo = new() { Name = "webos-mcp", Version = "1.0.0" })
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(StdioServerHost).Assembly);

        return builder.Build();
    }
}

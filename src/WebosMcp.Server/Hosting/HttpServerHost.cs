using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebosMcp.Server.Configuration;

namespace WebosMcp.Server.Hosting;

public static class HttpServerHost
{
    /// <summary>
    /// Builds the Streamable HTTP host. <paramref name="configureBuilder"/> lets
    /// the test suite substitute an in-memory server; production passes null.
    /// </summary>
    public static WebApplication Build(
        HttpTransportSettings settings,
        string[] args,
        Action<WebApplicationBuilder>? configureBuilder = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options => options.SingleLine = true);

        builder.Services.AddSingleton(settings);
        builder.Services.AddWebosMcp(builder.Configuration);

        // The same shared tool layer stdio serves — discovered from this assembly.
        builder.Services
            .AddMcpServer(options => options.ServerInfo = new() { Name = "webos-mcp", Version = "1.0.0" })
            .WithHttpTransport()
            .WithToolsFromAssembly(typeof(HttpServerHost).Assembly);

        configureBuilder?.Invoke(builder);

        if (configureBuilder is null)
        {
            builder.WebHost.UseUrls(settings.Url);
        }

        var app = builder.Build();

        // Auth runs ahead of everything, including the MCP endpoint mapping.
        app.UseMiddleware<BearerTokenMiddleware>();
        app.MapMcp();

        return app;
    }
}

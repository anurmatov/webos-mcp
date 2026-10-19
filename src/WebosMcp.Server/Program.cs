using WebosMcp.Server.Configuration;
using WebosMcp.Server.Hosting;

namespace WebosMcp.Server;

public static class Program
{
    private const string Usage = """
        webos-mcp — MCP server for LG webOS TV control

        Usage:
          webos-mcp [stdio]        Serve MCP over stdio (default).
          webos-mcp http           Serve MCP over Streamable HTTP.
          webos-mcp discover       Find LG webOS TVs on the local network.
          webos-mcp pair           Pair with the configured TV (accept the on-screen prompt).
          webos-mcp status         Show configuration, pairing state and TV power state.

        Configuration is read from the environment; see the README for the full reference.
        """;

    public static async Task<int> Main(string[] args)
    {
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "stdio";
        var rest = args.Length > 1 ? args[1..] : [];

        using var lifetime = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            lifetime.Cancel();
        };

        switch (command)
        {
            case "-h":
            case "--help":
            case "help":
                Console.WriteLine(Usage);
                return 0;

            case "discover":
            case "pair":
            case "status":
                return await OperatorCommands.RunAsync(command, rest, lifetime.Token).ConfigureAwait(false);

            case "http":
                return await RunHttpAsync(rest).ConfigureAwait(false);

            case "stdio":
                await StdioServerHost.Build(rest).RunAsync(lifetime.Token).ConfigureAwait(false);
                return 0;

            default:
                Console.Error.WriteLine($"Unknown command '{args[0]}'.");
                Console.Error.WriteLine();
                Console.Error.WriteLine(Usage);
                return 64;
        }
    }

    private static async Task<int> RunHttpAsync(string[] args)
    {
        HttpTransportSettings settings;
        WebApplication app;
        try
        {
            settings = HttpTransportSettings.Resolve(HttpTransportSettings.CurrentEnvironment());
            app = HttpServerHost.Build(settings, args);
        }
        catch (HttpTransportConfigurationException ex)
        {
            // Fail to start rather than serve unauthenticated control to the network.
            Console.Error.WriteLine(ex.Message);
            return 78;
        }

        var mode = (settings.UnixSocketPath is not null, settings.RequiresAuth) switch
        {
            (true, true) => $"unix socket mode {Convert.ToString((int)settings.UnixSocketMode, 8).PadLeft(4, '0')}, bearer token required",
            (true, false) => $"unix socket mode {Convert.ToString((int)settings.UnixSocketMode, 8).PadLeft(4, '0')}, no token configured",
            (false, true) => "bearer token required",
            (false, false) => "loopback-only, no token configured",
        };

        await HttpServerHost.StartAsync(app, settings, CancellationToken.None).ConfigureAwait(false);
        Console.Error.WriteLine($"webos-mcp listening on {settings.Url} ({mode}).");

        await app.WaitForShutdownAsync().ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
        return 0;
    }
}

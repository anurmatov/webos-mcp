using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using WebosMcp.Application;
using WebosMcp.Domain;
using WebosMcp.Infrastructure;

namespace WebosMcp.Server.Hosting;

/// <summary>
/// Operator bootstrap commands. These are run by a human at a terminal and are
/// deliberately NOT MCP tools — pairing in particular requires physical access
/// to accept the on-screen prompt.
/// </summary>
public static class OperatorCommands
{
    public static async Task<int> RunAsync(string command, string[] args, CancellationToken cancellationToken)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
        builder.Logging.Services.Configure<ConsoleLoggerOptions>(
            options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddWebosMcp(builder.Configuration);
        using var host = builder.Build();

        return command switch
        {
            "discover" => await DiscoverAsync(host.Services, cancellationToken).ConfigureAwait(false),
            "pair" => await PairAsync(host.Services, cancellationToken).ConfigureAwait(false),
            "status" => await StatusAsync(host.Services, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unknown operator command."),
        };
    }

    private static async Task<int> DiscoverAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var discovery = services.GetRequiredService<ITvDiscovery>();
        Console.WriteLine("Searching for LG webOS TVs on the local network (5s)...");

        var found = await discovery.DiscoverAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        if (found.Count == 0)
        {
            Console.WriteLine("No TVs found. Check that the TV is powered on and on the same network segment.");
            return 1;
        }

        foreach (var tv in found)
        {
            Console.WriteLine($"  {tv.Address}  {tv.FriendlyName ?? "(no name)"}  {tv.ModelName ?? string.Empty}".TrimEnd());
        }

        Console.WriteLine();
        Console.WriteLine("Set WEBOSMCP__HOST to the address above, then run: webos-mcp pair");
        return 0;
    }

    private static async Task<int> PairAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var pairing = services.GetRequiredService<PairingService>();

        try
        {
            Console.WriteLine("Pairing. Accept the prompt that appears on the TV screen...");

            // Same service the pair_device MCP tool uses — one pairing path.
            var outcome = await pairing.PairAsync(force: false, cancellationToken).ConfigureAwait(false);

            if (outcome.AlreadyPaired)
            {
                Console.WriteLine($"Already paired. The client key is stored at: {outcome.Location}");
                return 0;
            }

            // The location, never the key.
            Console.WriteLine($"Paired. The client key is stored at: {outcome.Location}");
            Console.WriteLine("Verified on disk. Keep this value private — it grants full control of the TV.");
            return 0;
        }
        catch (TvException ex)
        {
            Console.Error.WriteLine($"Pairing failed [{ex.Code.ToWireCode()}]: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> StatusAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var options = services.GetRequiredService<IOptions<WebosMcpOptions>>().Value;
        var keyStore = services.GetRequiredService<IClientKeyStore>();
        var session = services.GetRequiredService<ITvSession>();
        var control = services.GetRequiredService<TvControlService>();

        Console.WriteLine($"Host           : {options.Host ?? "(not configured)"}:{options.Port}");
        Console.WriteLine($"MAC address    : {options.MacAddress ?? "(not configured)"}");
        Console.WriteLine($"Broadcast      : {options.BroadcastAddress}");
        Console.WriteLine($"Client key     : {keyStore.DescribeLocation()}");

        var paired = await session.IsPairedAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Paired         : {(paired ? "yes" : "no — run 'webos-mcp pair'")}");

        if (!paired)
        {
            return 1;
        }

        try
        {
            var state = await control.GetPowerStateAsync(cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Power state    : {state}");
            return 0;
        }
        catch (TvException ex)
        {
            Console.WriteLine($"Power state    : unavailable [{ex.Code.ToWireCode()}] {ex.Message}");
            return 1;
        }
    }
}

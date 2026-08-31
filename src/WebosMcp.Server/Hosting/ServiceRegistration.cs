using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebosMcp.Application;
using WebosMcp.Infrastructure;
using WebosMcp.Server.Tools;

namespace WebosMcp.Server.Hosting;

public static class ServiceRegistration
{
    /// <summary>
    /// The single shared composition root. Both transports and every operator
    /// CLI command register exactly these services — there is no
    /// transport-specific tool wiring anywhere.
    /// </summary>
    public static IServiceCollection AddWebosMcp(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WebosMcpOptions>(configuration.GetSection(WebosMcpOptions.SectionName));

        services.AddSingleton<ISsapConnectionFactory, SsapConnectionFactory>();
        services.AddSingleton<IWolSender, UdpWolSender>();
        services.AddSingleton<IClientKeyStore, FileClientKeyStore>();
        services.AddSingleton<ITvDiscovery, SsdpTvDiscovery>();
        services.AddSingleton<IDeviceStore, FileDeviceStore>();
        services.AddSingleton<INetworkFacts, SystemNetworkFacts>();
        services.AddSingleton<DeviceService>();

        // The active device is applied before anything reads Host/MAC, so a
        // selection made through MCP survives a restart without an env var.
        services.AddHostedService<ActiveDeviceApplier>();
        services.AddSingleton<ISsdpChannel, UdpSsdpChannel>();

        // DIAL is a third protocol alongside SSAP and WOL; it gets a plain
        // HttpClient with a short timeout, since every call is LAN-local.
        services.AddHttpClient<IDialClient, DialClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("webos-mcp/1.0");
        });
        // Lounge is the ONE component that leaves the LAN: controlling a running
        // YouTube receiver requires Google's service. Timeout is generous because
        // the event channel is a long poll.
        services.AddHttpClient<ILoungeClient, LoungeClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("webos-mcp/1.0");
        });

        // The screenshot download gets its OWN client and its own primary handler.
        // Two properties depend on that isolation and would be wrong on a shared
        // one: redirects are followed manually so every hop can be re-pinned to the
        // TV, and a self-signed certificate is tolerated ONLY for the selected TV's
        // host. Neither leaks to the DIAL or Lounge clients, and TLS validation is
        // never globally disabled.
        services
            .AddHttpClient<IScreenshotDownloader, ScreenshotDownloader>(client =>
            {
                // The downloader applies its own bounded timeout; a second,
                // independent one here would report the wrong failure.
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("webos-mcp/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(provider =>
            {
                var options = provider.GetRequiredService<
                    Microsoft.Extensions.Options.IOptions<WebosMcpOptions>>();

                return new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                        errors == System.Net.Security.SslPolicyErrors.None ||
                        ScreenshotPolicy.IsSelectedTvHost(request.RequestUri, options.Value),
                };
            });

        services.AddSingleton<IDelayProvider, RealDelayProvider>();

        services.AddSingleton<ITvSession, TvSession>();
        services.AddSingleton<TvControlService>();
        services.AddSingleton<PowerService>();
        services.AddSingleton<PairingService>();

        return services;
    }

    /// <summary>
    /// Registers the shared tool layer. <c>pair_device</c> is added ONLY when
    /// the operator has opted in: on a default deployment it is never
    /// registered, so it cannot appear in tools/list or be called at all.
    /// Both transports go through here, so neither can drift from the other.
    /// </summary>
    public static IMcpServerBuilder AddWebosMcpTools(
        this IMcpServerBuilder builder,
        IConfiguration configuration)
    {
        builder.WithToolsFromAssembly(typeof(ServiceRegistration).Assembly);

        var enablePairing = configuration
            .GetSection(WebosMcpOptions.SectionName)
            .GetValue<bool>(nameof(WebosMcpOptions.EnablePairingTool));

        if (enablePairing)
        {
            builder.WithTools<PairingTools>();
        }

        return builder;
    }
}

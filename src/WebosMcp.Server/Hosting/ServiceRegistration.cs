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
        services.AddSingleton<ISsdpChannel, UdpSsdpChannel>();

        // DIAL is a third protocol alongside SSAP and WOL; it gets a plain
        // HttpClient with a short timeout, since every call is LAN-local.
        services.AddHttpClient<IDialClient, DialClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("webos-mcp/1.0");
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

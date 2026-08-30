using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebosMcp.Application;
using WebosMcp.Infrastructure;

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
        services.AddSingleton<IDelayProvider, RealDelayProvider>();

        services.AddSingleton<ITvSession, TvSession>();
        services.AddSingleton<TvControlService>();
        services.AddSingleton<PowerService>();
        services.AddSingleton<PairingService>();

        return services;
    }
}

using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebosMcp.Application;

namespace WebosMcp.Infrastructure;

public sealed class SsapConnectionFactory : ISsapConnectionFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly WebosMcpOptions _options;

    public SsapConnectionFactory(ILoggerFactory loggerFactory, IOptions<WebosMcpOptions> options)
    {
        _loggerFactory = loggerFactory;
        _options = options.Value;
    }

    public ISsapConnection Create(IPEndPoint endpoint, bool useTls) => new SsapWebSocketConnection(
        endpoint,
        useTls,
        TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds),
        _loggerFactory.CreateLogger<SsapWebSocketConnection>());
}

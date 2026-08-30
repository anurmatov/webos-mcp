using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebosMcp.Application;
using WebosMcp.Domain;

namespace WebosMcp.Infrastructure;

/// <summary>
/// Operator bootstrap pairing. Deliberately NOT an MCP tool and not reachable
/// from either transport — pairing requires physical access to accept the
/// on-screen prompt.
/// </summary>
public sealed class PairingService
{
    private readonly ISsapConnectionFactory _factory;
    private readonly IClientKeyStore _keyStore;
    private readonly WebosMcpOptions _options;
    private readonly ILogger<PairingService> _logger;

    public PairingService(
        ISsapConnectionFactory factory,
        IClientKeyStore keyStore,
        IOptions<WebosMcpOptions> options,
        ILogger<PairingService> logger)
    {
        _factory = factory;
        _keyStore = keyStore;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Returns where the key was stored — never the key itself.</summary>
    public async Task<string> PairAsync(CancellationToken cancellationToken)
    {
        var endpoint = _options.RequireEndpoint();
        _logger.LogInformation(
            "Connecting to {Endpoint}. Accept the pairing prompt on the TV screen.", endpoint);

        await using var connection = _factory.Create(endpoint, _options.UseTls);
        await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);

        var existing = await _keyStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        string clientKey;
        try
        {
            clientKey = await connection.RegisterAsync(existing, cancellationToken).ConfigureAwait(false);
        }
        catch (TvException ex) when (ex.Code == TvErrorCode.PairingRequired && existing is not null)
        {
            _logger.LogWarning("The stored client key was rejected; re-pairing from scratch.");
            clientKey = await connection.RegisterAsync(null, cancellationToken).ConfigureAwait(false);
        }

        await _keyStore.WriteAsync(clientKey, cancellationToken).ConfigureAwait(false);
        return _keyStore.DescribeLocation();
    }
}

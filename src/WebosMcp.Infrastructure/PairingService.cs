using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebosMcp.Application;
using WebosMcp.Domain;

namespace WebosMcp.Infrastructure;

/// <summary>
/// The single pairing implementation, shared by the <c>pair</c> operator CLI
/// command and the opt-in <c>pair_device</c> MCP tool. There is deliberately
/// only one code path: two implementations would drift, and the durability and
/// secret-handling guarantees below are exactly the kind that rot silently.
///
/// Guarantees:
///   - pairing always requires a human to accept the on-screen prompt;
///   - the key is persisted atomically and RE-READ FROM DISK before success is
///     reported;
///   - the key is never returned, logged, or placed in an exception message —
///     callers get the storage location only.
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

    /// <summary>
    /// Pairs with the configured TV and durably persists the key.
    /// </summary>
    /// <param name="force">
    /// When false, an existing key that the TV still accepts short-circuits to
    /// an already-paired result rather than prompting a human for nothing.
    /// </param>
    public async Task<PairingOutcome> PairAsync(bool force, CancellationToken cancellationToken)
    {
        var endpoint = _options.RequireEndpoint();

        // Fail on an unwritable destination BEFORE sending anyone to the TV.
        // Discovering it afterwards wastes a physical trip and, worse, would
        // pair successfully and then lose the key.
        var destination = _keyStore.DurableWritablePath;
        if (string.IsNullOrWhiteSpace(destination))
        {
            throw TvException.KeyStorageReadOnly(
                $"No durable writable key location is configured. The current key source ({_keyStore.DescribeLocation()}) " +
                "is read-only to this process, so a new key could not be kept. Set WEBOSMCP__CLIENTKEYPATH to a " +
                "writable path and retry before accepting anything on the TV.");
        }

        var existing = await _keyStore.ReadAsync(cancellationToken).ConfigureAwait(false);

        if (!force && !string.IsNullOrWhiteSpace(existing))
        {
            if (await IsExistingKeyAcceptedAsync(endpoint, existing!, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation("Already paired; no on-screen prompt was raised.");
                return new PairingOutcome(
                    AlreadyPaired: true,
                    Location: _keyStore.DescribeLocation(),
                    VerifiedOnDisk: true);
            }

            _logger.LogInformation("The stored client key was refused by the TV; re-pairing from scratch.");
        }

        _logger.LogInformation(
            "Requesting pairing with {Endpoint}. A human must accept the prompt on the TV within {Timeout}s.",
            endpoint,
            _options.PairingTimeoutSeconds);

        await using var connection = _factory.Create(endpoint, _options.UseTls);
        await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);

        // Pairing gets its own, much longer budget than an ordinary request:
        // somebody has to physically walk to the TV.
        using var prompt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        prompt.CancelAfter(TimeSpan.FromSeconds(_options.PairingTimeoutSeconds));

        string clientKey;
        try
        {
            // Always register WITHOUT a key here, so an SSAP error can only mean
            // the prompt was declined — never "the stale key was refused".
            clientKey = await connection.RegisterAsync(null, prompt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw TvException.PairingTimedOut(_options.PairingTimeoutSeconds);
        }

        // Persist atomically and verify from disk. Only then is this a success.
        var location = await _keyStore.PersistAsync(clientKey, cancellationToken).ConfigureAwait(false);

        return new PairingOutcome(AlreadyPaired: false, Location: location, VerifiedOnDisk: true);
    }

    /// <summary>
    /// Probes whether the stored key still works, without raising a prompt.
    /// Any failure answers "no" — the caller then pairs for real, which is the
    /// safe direction to be wrong in.
    /// </summary>
    private async Task<bool> IsExistingKeyAcceptedAsync(
        System.Net.IPEndPoint endpoint,
        string existing,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var probe = _factory.Create(endpoint, _options.UseTls);
            await probe.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await probe.RegisterAsync(existing, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TvException ex) when (ex.Code is TvErrorCode.PairingRequired or TvErrorCode.PairingDenied)
        {
            return false;
        }
    }
}

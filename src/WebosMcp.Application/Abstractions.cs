using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using WebosMcp.Domain;

namespace WebosMcp.Application;

/// <summary>
/// The SSAP control channel. Every network boundary the server touches sits
/// behind this interface so the whole suite runs in CI with no physical TV.
/// </summary>
public interface ISsapConnection : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>Opens the socket. Throws <see cref="TvException"/> with TV_OFF / TV_UNREACHABLE.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Performs the SSAP register handshake. Returns the client key the TV
    /// accepted (the supplied one, or a newly issued one when
    /// <paramref name="clientKey"/> is null). Throws PAIRING_REQUIRED when the
    /// TV rejects the supplied key.
    /// </summary>
    Task<string> RegisterAsync(string? clientKey, CancellationToken cancellationToken);

    /// <summary>Issues a request/response SSAP call and returns the <c>payload</c> object.</summary>
    Task<JsonElement> RequestAsync(string uri, object? payload, CancellationToken cancellationToken);

    Task SendButtonAsync(string wireName, CancellationToken cancellationToken);

    Task SendPointerMoveAsync(int deltaX, int deltaY, bool drag, CancellationToken cancellationToken);

    Task SendPointerClickAsync(CancellationToken cancellationToken);

    Task SendPointerScrollAsync(int deltaX, int deltaY, CancellationToken cancellationToken);
}

public interface ISsapConnectionFactory
{
    ISsapConnection Create(IPEndPoint endpoint, bool useTls);
}

/// <summary>Sends Wake-on-LAN magic packets. Returns the target addresses actually written to.</summary>
public interface IWolSender
{
    Task<IReadOnlyList<string>> SendAsync(
        PhysicalAddress mac,
        IReadOnlyList<IPEndPoint> targets,
        CancellationToken cancellationToken);
}

/// <summary>Reads and writes the pairing client key. Never logged, never returned by a tool.</summary>
public interface IClientKeyStore
{
    Task<string?> ReadAsync(CancellationToken cancellationToken);

    Task WriteAsync(string clientKey, CancellationToken cancellationToken);

    /// <summary>Human-readable description of where the key lives — never the key itself.</summary>
    string DescribeLocation();
}

public sealed record DiscoveredTv(string Address, string? FriendlyName, string? ModelName);

public interface ITvDiscovery
{
    Task<IReadOnlyList<DiscoveredTv>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Abstracted so fallback-sequence pacing is instant and deterministic in tests.</summary>
public interface IDelayProvider
{
    Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken);
}

public sealed class RealDelayProvider : IDelayProvider
{
    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        duration <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(duration, cancellationToken);
}

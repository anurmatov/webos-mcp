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

    /// <summary>
    /// Best-effort update used when the TV reissues a key mid-session. May be a
    /// cache-only update when no durable writable location is configured; it
    /// logs when that happens rather than short-circuiting silently.
    /// </summary>
    Task WriteAsync(string clientKey, CancellationToken cancellationToken);

    /// <summary>
    /// Durably persists the key for pairing: writes atomically to the configured
    /// writable location, then RE-READS IT FROM DISK and verifies the content
    /// before returning. Returns the storage location — never the key.
    /// Throws <see cref="TvException"/> with
    /// <see cref="TvErrorCode.KeyStorageReadOnly"/> when no writable location is
    /// configured or the write cannot be made durable.
    /// </summary>
    Task<string> PersistAsync(string clientKey, CancellationToken cancellationToken);

    /// <summary>The configured durable writable path, or null when there is none.</summary>
    string? DurableWritablePath { get; }

    /// <summary>Human-readable description of where the key lives — never the key itself.</summary>
    string DescribeLocation();
}

public sealed record DiscoveredTv(string Address, string? FriendlyName, string? ModelName);

/// <summary>State of a DIAL application on the TV.</summary>
/// <param name="AllowStop">
/// The app's DIAL <c>allowStop</c> option. Without it the app cannot be stopped
/// over DIAL, and therefore cannot be cold-started with a new video.
/// </param>
/// <param name="RunLink">
/// The <c>rel="run"</c> href of a running instance, relative to the app URL. This
/// is the only address a DIAL stop can be sent to.
/// </param>
public sealed record DialAppStatus(
    string Name,
    string State,
    bool Installed,
    bool AllowStop = false,
    string? RunLink = null)
{
    public bool IsRunning => State.Equals("running", StringComparison.OrdinalIgnoreCase)
        || State.Equals("starting", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a running instance can be stopped, which is what makes a cold restart possible.</summary>
    public bool CanStop => AllowStop && !string.IsNullOrWhiteSpace(RunLink);
}

/// <summary>
/// DIAL (DIscovery And Launch) — the third protocol this server speaks, after
/// SSAP and Wake-on-LAN. It exists because SSAP's launcher accepts a YouTube
/// launch request and returns success while the TV stays on the home screen;
/// DIAL gives a launch that can actually be confirmed.
/// </summary>
public interface IDialClient
{
    /// <summary>
    /// Resolves the TV's DIAL application URL, or null when the TV exposes no
    /// DIAL endpoint (in which case the capability is genuinely unsupported).
    /// </summary>
    Task<Uri?> ResolveApplicationUrlAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads an application's DIAL status. Null means, and only means, the app is
    /// not installed (DIAL 404). Any other refusal — notably a 403, which is an
    /// origin/authorisation failure and says nothing about whether the app is
    /// installed — is reported as <see cref="TvErrorCode.TvError"/> naming the
    /// HTTP status, never flattened into "not installed".
    /// </summary>
    Task<DialAppStatus?> GetAppStatusAsync(Uri applicationUrl, string app, CancellationToken cancellationToken);

    /// <summary>
    /// POSTs a launch request. Returns true only when the TV accepted it
    /// (2xx). Acceptance alone is NOT treated as success by callers. A rejection
    /// is reported as <see cref="TvErrorCode.TvError"/> naming the HTTP status;
    /// returning false is also permitted for implementations with no status to
    /// report.
    /// </summary>
    Task<bool> LaunchAppAsync(
        Uri applicationUrl,
        string app,
        string payload,
        CancellationToken cancellationToken);

    /// <summary>
    /// DELETEs a running app instance. Returns true when the TV accepted the stop.
    /// Needed because launching over an already-running app does NOT change what it
    /// is playing — the payload is ignored — so the only way to make a requested
    /// video take effect is to stop the app and start it cold.
    /// </summary>
    Task<bool> StopAppAsync(
        Uri applicationUrl,
        string app,
        string runLink,
        CancellationToken cancellationToken);
}

public interface ITvDiscovery
{
    Task<IReadOnlyList<DiscoveredTv>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Sends one SSDP M-SEARCH and collects the raw responses received before the
/// window closes. Abstracted so DIAL resolution — including the unicast search
/// that replaces multicast inside a container — is exercised in CI with no TV
/// and no multicast-capable network.
/// </summary>
public interface ISsdpChannel
{
    /// <param name="target">
    /// Where to send. A unicast TV address works on networks that drop multicast;
    /// 239.255.255.250:1900 is the conventional multicast group.
    /// </param>
    Task<IReadOnlyList<string>> SearchAsync(
        System.Net.IPEndPoint target,
        string searchTarget,
        TimeSpan window,
        CancellationToken cancellationToken);
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

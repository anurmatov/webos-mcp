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

    /// <summary>
    /// True when the stored key was granted under a DIFFERENT permission set than
    /// the one this build presents — including a key stored before the permission
    /// set was recorded at all, which is every key paired by an earlier version.
    ///
    /// This is reported, never acted on. A changed manifest does not retroactively
    /// widen an existing grant, and the server must not re-pair to obtain the new
    /// permissions: pairing requires a human at the TV, so a background re-pair
    /// would either fail or, worse, train someone to approve prompts they did not
    /// ask for. The one legitimate use is telling an operator WHY a command was
    /// denied and what explicit action would fix it.
    /// </summary>
    Task<bool> IsGrantStaleAsync(CancellationToken cancellationToken);

    /// <summary>The configured durable writable path, or null when there is none.</summary>
    string? DurableWritablePath { get; }

    /// <summary>Human-readable description of where the key lives — never the key itself.</summary>
    string DescribeLocation();
}

public sealed record DiscoveredTv(string Address, string? FriendlyName, string? ModelName);

/// <summary>State of a DIAL application on the TV.</summary>
/// <param name="ScreenId">
/// The receiver's Lounge screen id, from the status document's
/// <c>additionalData</c>. This is what DIAL is actually for here: it is the handle
/// used to obtain a Lounge token and control the running YouTube receiver. Absent
/// means the receiver cannot be controlled.
/// </param>
public sealed record DialAppStatus(
    string Name,
    string State,
    bool Installed,
    string? ScreenId = null)
{
    public bool IsRunning => State.Equals("running", StringComparison.OrdinalIgnoreCase)
        || State.Equals("starting", StringComparison.OrdinalIgnoreCase);
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

}

/// <summary>
/// One state report from the receiver — now-playing, plus the volume and autoplay
/// changes it also announces. Every field is optional because a report only carries
/// what changed, and a field the receiver did not send is left null rather than
/// guessed at.
/// </summary>
public sealed record LoungeReceiverState(
    string? VideoId = null,
    LoungePlayerState State = LoungePlayerState.Unknown,
    double? CurrentTime = null,
    double? Duration = null,
    int? Volume = null,
    bool? AutoplayEnabled = null,
    string? EventName = null);

/// <summary>
/// Folds the receiver's reports into one running picture of what is playing.
///
/// The receiver does NOT announce "video X is now playing" in a single event. It
/// sends <c>nowPlaying</c> carrying the video id — often still buffering — and then
/// a separate <c>onStateChange</c> carrying the playing state WITH NO VIDEO ID.
/// Requiring both in one report therefore never matches, and the tool reports a
/// video that is visibly playing as never observed. That is a false negative, and it
/// is the third distinct way this path produced one.
///
/// An id-less state report applies to the video most recently announced, so the two
/// are correlated here rather than in a predicate that can only see one event.
/// </summary>
public sealed class ReceiverStateTracker
{
    private string? _videoId;
    private LoungePlayerState _state = LoungePlayerState.Unknown;
    private double? _currentTime;

    /// <summary>
    /// Applies one report and returns the composite picture after it.
    ///
    /// A report naming a DIFFERENT video resets the state rather than inheriting the
    /// previous one — otherwise a stale "playing" would immediately be attributed to
    /// a video that has only just been announced and may still be loading.
    /// </summary>
    public LoungeReceiverState Apply(LoungeReceiverState report)
    {
        if (report.VideoId is { Length: > 0 } id)
        {
            if (!string.Equals(id, _videoId, StringComparison.Ordinal))
            {
                _videoId = id;
                _state = report.State;
                _currentTime = report.CurrentTime;
            }
            else
            {
                if (report.State != LoungePlayerState.Unknown)
                {
                    _state = report.State;
                }

                _currentTime = report.CurrentTime ?? _currentTime;
            }
        }
        else if (report.State != LoungePlayerState.Unknown)
        {
            // Applies to whatever was last announced. With nothing announced yet it
            // is deliberately unattributable — _videoId stays null and no request
            // can match it.
            _state = report.State;
            _currentTime = report.CurrentTime ?? _currentTime;
        }
        else
        {
            _currentTime = report.CurrentTime ?? _currentTime;
        }

        return new LoungeReceiverState(
            _videoId,
            _state,
            _currentTime,
            report.Duration,
            report.Volume,
            report.AutoplayEnabled,
            report.EventName);
    }
}

/// <summary>YouTube receiver player states, as the Lounge protocol reports them.</summary>
public enum LoungePlayerState
{
    Unknown = -2,
    Unstarted = -1,
    Ended = 0,
    Playing = 1,
    Paused = 2,
    Buffering = 3,
    Cued = 5,
}

/// <summary>
/// A connected remote-control session against one YouTube receiver.
///
/// This is the only mechanism that can load a specific video into an ALREADY
/// RUNNING receiver, and the only one that reports back which video is actually
/// playing. DIAL can do neither: a DIAL launch aimed at a running app is accepted
/// and ignored, and DIAL exposes no read-back of the playing video.
/// </summary>
public interface ILoungeSession : IAsyncDisposable
{
    /// <summary>Sends one Lounge command. Acceptance is NOT playback — callers must observe.</summary>
    Task SendAsync(
        string command,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens the receiver's event stream, starts consuming it, and returns only once
    /// a read is actually OUTSTANDING on it.
    ///
    /// The ordering this enables is the whole point: a command must never be sent
    /// before something is listening for its effect. The receiver announces a state
    /// change once, as it happens, to whoever is reading at that instant — so a
    /// stream opened afterwards, or merely accepted but unread, can miss it entirely.
    /// That is how a video which really did start playing was reported as never
    /// observed.
    ///
    /// Response headers are NOT the barrier. They say the request was accepted, not
    /// that anyone is reading. Nor is a sleep: it asserts a delay rather than
    /// confirming a fact. This returning is the barrier, and it means the pump is
    /// running.
    /// </summary>
    Task<ILoungeSubscription> SubscribeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// An event stream that is already open. Obtained before the command whose effect
/// it is meant to observe, so nothing the receiver announces can fall between them.
/// </summary>
public interface ILoungeSubscription : IAsyncDisposable
{
    /// <summary>
    /// The receiver's own state reports, as they arrive on the stream that was
    /// already open. This is the observation every YouTube tool's success is proven
    /// against.
    /// </summary>
    IAsyncEnumerable<LoungeReceiverState> ReadAsync(CancellationToken cancellationToken);
}

public interface ILoungeClient
{
    /// <summary>
    /// Connects as a remote to the receiver with this screen id. Null when the
    /// receiver cannot be controlled, which callers report as unsupported rather
    /// than falling back to something unverifiable.
    /// </summary>
    Task<ILoungeSession?> ConnectAsync(string screenId, CancellationToken cancellationToken);
}

/// <summary>
/// Fetches the capture the TV announced. Behind an interface for the same reason
/// every other network boundary is: the whole suite runs in CI with no TV, and
/// the redirect, size and timeout rules are exercised without a real socket.
///
/// Implementations MUST re-pin every hop to the selected TV, cap the body size
/// while streaming, and never write the bytes anywhere.
/// </summary>
public interface IScreenshotDownloader
{
    Task<ReadOnlyMemory<byte>> DownloadAsync(Uri imageUri, CancellationToken cancellationToken);
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

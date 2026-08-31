namespace WebosMcp.Domain;

public sealed record AppInfo(string Id, string Title, string? Version = null, bool System = false);

public sealed record ForegroundApp(string? AppId, string? WindowId, string? ProcessId);

public sealed record SoftwareInfo(
    string? ModelName,
    string? FirmwareVersion,
    string? MajorVersion,
    string? MinorVersion,
    string? ProductName);

public sealed record SystemInfo(string? ModelName, string? ReceiverType, string? Features);

public sealed record VolumeState(int Volume, bool Muted, string? SoundOutput, int? MinVolume, int? MaxVolume);

public sealed record ExternalInput(string Id, string Label, bool Connected, string? Icon = null);

public sealed record ChannelInfo(
    string? ChannelId,
    string? ChannelNumber,
    string? ChannelName,
    string? ProgramName);

/// <summary>
/// Result of a power-on attempt. <paramref name="Verified"/> is false when the
/// magic packet was sent but the TV never reached an Active-equivalent state
/// within the timeout — that is reported as an explicit unverified result,
/// never as an optimistic success.
/// </summary>
public sealed record PowerOnResult(
    bool Verified,
    PowerState FinalState,
    bool AlreadyOn,
    int MagicPacketsSent,
    IReadOnlyList<string> SentTo,
    double ElapsedSeconds,
    string Detail);

/// <summary>
/// Result of a pairing attempt. Deliberately carries the STORAGE LOCATION and
/// never the client key itself — the key is not returned by any tool, logged,
/// or included in any error message.
/// </summary>
public sealed record PairingOutcome(
    bool AlreadyPaired,
    string Location,
    bool VerifiedOnDisk);

/// <summary>
/// What was actually observed when launching content, as opposed to what was
/// merely requested. Physical testing showed a launcher call being accepted
/// while the TV stayed on the home screen, so "the TV did not say no" is not
/// evidence of anything and is never reported as success.
/// </summary>
public sealed record LaunchEvidence(
    bool DialEndpointFound,
    bool DialLaunchAccepted,
    bool ForegroundConfirmed,
    string? ForegroundAppId,
    double ElapsedSeconds);

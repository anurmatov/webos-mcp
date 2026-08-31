namespace WebosMcp.Domain;

/// <summary>
/// Machine-checkable error codes. Callers must be able to distinguish these
/// without string-matching a free-form message.
/// </summary>
public enum TvErrorCode
{
    /// <summary>No valid client key present, or the TV rejected the stored key.</summary>
    PairingRequired,

    /// <summary>The TV is reachable on the network but powered off / in standby.</summary>
    TvOff,

    /// <summary>The TV could not be reached at all (no route, no response, socket failure).</summary>
    TvUnreachable,

    /// <summary>The TV is connected but does not support this capability on this input/model.</summary>
    TvUnsupportedCapability,

    /// <summary>The request was rejected before touching the TV because an input failed validation.</summary>
    InvalidInput,

    /// <summary>The operation exceeded its bounded timeout.</summary>
    Timeout,

    /// <summary>The TV returned an error that does not map to a more specific code.</summary>
    TvError,

    /// <summary>The pairing tool is not enabled on this deployment.</summary>
    PairingDisabled,

    /// <summary>A human actively declined the pairing prompt on the TV.</summary>
    PairingDenied,

    /// <summary>Nobody answered the on-screen pairing prompt in time.</summary>
    PairingTimeout,

    /// <summary>The configured durable key location is missing or not writable.</summary>
    KeyStorageReadOnly,
}

public static class TvErrorCodeExtensions
{
    /// <summary>Stable wire representation used in tool responses. Never localise these.</summary>
    public static string ToWireCode(this TvErrorCode code) => code switch
    {
        TvErrorCode.PairingRequired => "PAIRING_REQUIRED",
        TvErrorCode.TvOff => "TV_OFF",
        TvErrorCode.TvUnreachable => "TV_UNREACHABLE",
        TvErrorCode.TvUnsupportedCapability => "TV_UNSUPPORTED_CAPABILITY",
        TvErrorCode.InvalidInput => "INVALID_INPUT",
        TvErrorCode.Timeout => "TIMEOUT",
        TvErrorCode.TvError => "TV_ERROR",
        TvErrorCode.PairingDisabled => "PAIRING_DISABLED",
        TvErrorCode.PairingDenied => "PAIRING_DENIED",
        TvErrorCode.PairingTimeout => "PAIRING_TIMEOUT",
        TvErrorCode.KeyStorageReadOnly => "KEY_STORAGE_READONLY",
        _ => "TV_ERROR",
    };
}

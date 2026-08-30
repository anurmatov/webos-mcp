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
        _ => "TV_ERROR",
    };
}

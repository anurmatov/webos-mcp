namespace WebosMcp.Domain;

/// <summary>
/// The single exception type crossing the application boundary. Carries a
/// machine-checkable <see cref="TvErrorCode"/> so callers never string-match.
/// </summary>
public sealed class TvException : Exception
{
    public TvErrorCode Code { get; }

    public TvException(TvErrorCode code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    public static TvException PairingRequired() => new(
        TvErrorCode.PairingRequired,
        "No valid client key is configured for this TV. Run the operator command 'webos-mcp pair' to pair, then restart the server.");

    public static TvException Off(string detail = "The TV is powered off or in standby.") =>
        new(TvErrorCode.TvOff, detail);

    public static TvException Unreachable(string detail, Exception? inner = null) =>
        new(TvErrorCode.TvUnreachable, detail, inner);

    public static TvException Unsupported(string capability) => new(
        TvErrorCode.TvUnsupportedCapability,
        $"This TV or the current input does not support '{capability}'.");

    /// <summary>
    /// The command was refused for lack of a granted capability, on a session
    /// that is registered and working. Carries the TV's own wording because that
    /// is what distinguishes one denied capability from another — and it must
    /// never be flattened into "no valid client key".
    /// </summary>
    public static TvException PermissionDenied(string detail) => new(
        TvErrorCode.TvPermissionDenied,
        $"The TV refused this command for lack of a granted permission: {detail}. " +
        "The pairing itself is intact — other commands still work — so re-pairing only helps if the " +
        "server's permission manifest has changed since this TV was paired.");

    public static TvException Invalid(string detail) => new(TvErrorCode.InvalidInput, detail);

    public static TvException TimedOut(string operation) =>
        new(TvErrorCode.Timeout, $"Operation '{operation}' did not complete within its timeout.");

    public static TvException PairingDisabled() => new(
        TvErrorCode.PairingDisabled,
        "Pairing over MCP is disabled on this deployment. It is opt-in: set WEBOSMCP__ENABLEPAIRINGTOOL=true and " +
        "configure a durable writable key location, or run the 'webos-mcp pair' operator command instead.");

    public static TvException PairingDenied() => new(
        TvErrorCode.PairingDenied,
        "The pairing request was declined on the TV. Accept the on-screen prompt to pair.");

    public static TvException PairingTimedOut(int seconds) => new(
        TvErrorCode.PairingTimeout,
        $"Nobody accepted the on-screen pairing prompt within {seconds}s. " +
        "Pairing needs a person at the TV; try again when someone can accept it.");

    public static TvException KeyStorageReadOnly(string detail) =>
        new(TvErrorCode.KeyStorageReadOnly, detail);
}

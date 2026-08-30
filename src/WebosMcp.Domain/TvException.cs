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

    public static TvException Invalid(string detail) => new(TvErrorCode.InvalidInput, detail);

    public static TvException TimedOut(string operation) =>
        new(TvErrorCode.Timeout, $"Operation '{operation}' did not complete within its timeout.");
}

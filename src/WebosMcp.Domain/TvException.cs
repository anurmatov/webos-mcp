namespace WebosMcp.Domain;

/// <summary>
/// The single exception type crossing the application boundary. Carries a
/// machine-checkable <see cref="TvErrorCode"/> so callers never string-match.
/// </summary>
public sealed class TvException : Exception
{
    /// <summary>
    /// How much TV-supplied wording is worth carrying. Enough to name a denied
    /// capability; not enough to flood a log line or a caller's display.
    /// </summary>
    public const int MaxDetailLength = 200;

    public TvErrorCode Code { get; }

    public TvException(TvErrorCode code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    /// <summary>
    /// Neutralises text the TV supplied before it is placed in a message.
    ///
    /// This text reaches two places that both matter: a caller's response, and a
    /// log line. It arrives from the network, so it is not ours and cannot be
    /// assumed well behaved. Left raw it can carry newlines that forge extra log
    /// entries, ANSI escapes that rewrite a terminal, NULs that truncate a
    /// consumer, or megabytes that bury everything around them.
    ///
    /// Control characters — which is what escapes, newlines and NUL all are —
    /// become spaces, runs of whitespace collapse, and the result is capped.
    /// Ordinary wording such as "401 insufficient permissions" passes through
    /// unchanged, because identifying which capability was refused is the entire
    /// value of keeping the detail at all.
    /// </summary>
    public static string SanitizeDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return "(no detail)";
        }

        var builder = new System.Text.StringBuilder(Math.Min(detail.Length, MaxDetailLength) + 1);
        var pendingSpace = false;

        foreach (var c in detail)
        {
            // Surrogates are dropped rather than spaced: a lone half is not a
            // character, and a pair carries nothing this message needs.
            if (char.IsSurrogate(c))
            {
                continue;
            }

            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            if (builder.Length >= MaxDetailLength)
            {
                return builder.ToString() + "…";
            }

            builder.Append(c);
        }

        return builder.Length == 0 ? "(no detail)" : builder.ToString();
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
    public static TvException PermissionDenied(string? detail) => new(
        TvErrorCode.TvPermissionDenied,
        $"The TV refused this command for lack of a granted permission: {SanitizeDetail(detail)}. " +
        "The pairing itself is intact — other commands still work — so re-pairing only helps if the " +
        "server's permission manifest has changed since this TV was paired.");

    /// <summary>
    /// The TV rejected a command for a reason with no more specific code. Carries
    /// the TV's own wording, sanitised.
    /// </summary>
    public static TvException Reported(TvErrorCode code, string? detail) =>
        new(code, $"The TV reported: {SanitizeDetail(detail)}");

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

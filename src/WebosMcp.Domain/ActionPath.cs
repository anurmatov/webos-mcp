namespace WebosMcp.Domain;

/// <summary>
/// Which execution path a content action actually took. Every content tool
/// response states this so a caller is never left guessing whether it got a
/// stable deep link or a best-effort remote-control sequence.
/// </summary>
public enum ActionPath
{
    /// <summary>A stable launch/deep-link parameter path was used.</summary>
    DeepLink,

    /// <summary>A bounded remote-control / text-entry sequence was used instead.</summary>
    Fallback,
}

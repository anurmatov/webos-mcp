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

    /// <summary>
    /// The DIAL protocol was used to launch the app, and the launch was
    /// confirmed by observing the app actually reach the foreground.
    /// </summary>
    Dial,
}

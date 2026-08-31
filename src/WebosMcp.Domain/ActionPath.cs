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

    /// <summary>
    /// The YouTube Lounge protocol was used to control the running receiver, and
    /// the receiver itself reported back the video id and player state. This is the
    /// only path that can confirm WHICH video is playing.
    /// </summary>
    Lounge,
}

namespace WebosMcp.Domain;

/// <summary>
/// Fixed allowlist of remote-control buttons. There is deliberately no
/// free-text key-name path — an MCP client cannot invent a button name.
/// </summary>
public enum RemoteButton
{
    Up,
    Down,
    Left,
    Right,
    Enter,
    Back,
    Exit,
    Home,
    Menu,
    Info,
    VolumeUp,
    VolumeDown,
    Mute,
    ChannelUp,
    ChannelDown,
    Play,
    Pause,
    Stop,
    Rewind,
    FastForward,
    Red,
    Green,
    Yellow,
    Blue,
    Num0,
    Num1,
    Num2,
    Num3,
    Num4,
    Num5,
    Num6,
    Num7,
    Num8,
    Num9,
    Dash,
}

public static class RemoteButtonExtensions
{
    /// <summary>Maps to the wire name webOS expects on the pointer input socket.</summary>
    public static string ToWireName(this RemoteButton button) => button switch
    {
        RemoteButton.Up => "UP",
        RemoteButton.Down => "DOWN",
        RemoteButton.Left => "LEFT",
        RemoteButton.Right => "RIGHT",
        RemoteButton.Enter => "ENTER",
        RemoteButton.Back => "BACK",
        RemoteButton.Exit => "EXIT",
        RemoteButton.Home => "HOME",
        RemoteButton.Menu => "MENU",
        RemoteButton.Info => "INFO",
        RemoteButton.VolumeUp => "VOLUMEUP",
        RemoteButton.VolumeDown => "VOLUMEDOWN",
        RemoteButton.Mute => "MUTE",
        RemoteButton.ChannelUp => "CHANNELUP",
        RemoteButton.ChannelDown => "CHANNELDOWN",
        RemoteButton.Play => "PLAY",
        RemoteButton.Pause => "PAUSE",
        RemoteButton.Stop => "STOP",
        RemoteButton.Rewind => "REWIND",
        RemoteButton.FastForward => "FASTFORWARD",
        RemoteButton.Red => "RED",
        RemoteButton.Green => "GREEN",
        RemoteButton.Yellow => "YELLOW",
        RemoteButton.Blue => "BLUE",
        RemoteButton.Num0 => "0",
        RemoteButton.Num1 => "1",
        RemoteButton.Num2 => "2",
        RemoteButton.Num3 => "3",
        RemoteButton.Num4 => "4",
        RemoteButton.Num5 => "5",
        RemoteButton.Num6 => "6",
        RemoteButton.Num7 => "7",
        RemoteButton.Num8 => "8",
        RemoteButton.Num9 => "9",
        RemoteButton.Dash => "DASH",
        _ => throw TvException.Invalid($"Unknown remote button '{button}'."),
    };
}

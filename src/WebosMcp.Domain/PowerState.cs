namespace WebosMcp.Domain;

/// <summary>Normalised power state. webOS reports several vendor strings that collapse to these.</summary>
public enum PowerState
{
    /// <summary>Screen on and the TV is serving requests.</summary>
    Active,

    /// <summary>Powered but the panel is off (screen-off / "Active Standby" style states).</summary>
    ScreenOff,

    /// <summary>Standby / suspended — not serving SSAP.</summary>
    Standby,

    /// <summary>Not reachable on the network.</summary>
    Unreachable,

    /// <summary>The TV answered but with a state string this server does not recognise.</summary>
    Unknown,
}

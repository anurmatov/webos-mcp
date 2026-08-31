namespace WebosMcp.Infrastructure;

/// <summary>
/// The permission manifest presented during pairing. Deliberately scoped to
/// the capabilities this server exposes — no raw-command or capture permissions.
/// </summary>
internal static class SsapManifest
{
    public static object Build() => new
    {
        manifestVersion = 1,
        appVersion = "1.0",
        signed = new
        {
            created = "20260101",
            appId = "com.anurmatov.webosmcp",
            vendorId = "com.anurmatov",
            localizedAppNames = new Dictionary<string, string>
            {
                ["" ] = "webos-mcp",
            },
            localizedVendorNames = new Dictionary<string, string>
            {
                ["" ] = "webos-mcp",
            },
            permissions = Permissions,
            serial = "0000000000000000",
        },
        permissions = Permissions,
challenge = string.Empty,
    };

    private static readonly string[] Permissions =
    [
        "LAUNCH",
        "LAUNCH_WEBAPP",
        "APP_TO_APP",
        "CLOSE",
        "TEST_OPEN",
        "TEST_PROTECTED",
        "CONTROL_AUDIO",
        "CONTROL_DISPLAY",
        "CONTROL_INPUT_JOYSTICK",
        "CONTROL_INPUT_MEDIA_RECORDING",
        "CONTROL_INPUT_MEDIA_PLAYBACK",
        "CONTROL_INPUT_TV",
        "CONTROL_POWER",
        "READ_APP_STATUS",
        "READ_CURRENT_CHANNEL",
        "READ_INPUT_DEVICE_LIST",
        "READ_NETWORK_STATE",
        "READ_TV_CHANNEL_LIST",
        "WRITE_NOTIFICATION_TOAST",
        "READ_POWER_STATE",
        "READ_INSTALLED_APPS",
        "CONTROL_INPUT_TEXT",
        "CONTROL_MOUSE_AND_KEYBOARD",
        "READ_UPDATE_INFO",
        "READ_SOFTWARE_INFO",
        "READ_SYSTEM_INFO",
    ];
}

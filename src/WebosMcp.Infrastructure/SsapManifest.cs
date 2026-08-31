using System.Security.Cryptography;
using System.Text;

namespace WebosMcp.Infrastructure;

/// <summary>
/// The permission manifest presented during pairing. Deliberately scoped to
/// the capabilities this server exposes — no raw-command or capture permissions.
///
/// ⚠️ The permission set is part of what the TV grants a key against. Changing it
/// does not retroactively widen an existing pairing: a key granted under the old
/// set keeps the old capabilities until a human re-pairs. That is why
/// <see cref="PermissionsFingerprint"/> exists and is recorded with the key —
/// so a denial can say "your pairing predates this permission" instead of leaving
/// an operator to guess, and WITHOUT the server ever re-pairing on its own.
/// </summary>
internal static class SsapManifest
{
    /// <summary>
    /// A stable short digest of the requested permission set. Order-independent,
    /// so reordering the list is correctly NOT treated as a change.
    ///
    /// It is a change-detector, not a secret: it identifies which permission set a
    /// key was granted under and reveals nothing about the key itself.
    /// </summary>
    public static string PermissionsFingerprint => FingerprintValue;

    internal static string ComputeFingerprint(IEnumerable<string> permissions)
    {
        var canonical = string.Join(
            '\n',
            permissions.Select(p => p.Trim()).OrderBy(p => p, StringComparer.Ordinal));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
    }

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

    /// <summary>
    /// Exposed so a test can pin the exact set. A permission appearing here that
    /// nothing in the closed <c>SsapUri</c> list needs is scope this server has
    /// not earned, and a missing one is a capability that will be denied at
    /// runtime with no way to tell why.
    /// </summary>
    internal static IReadOnlyList<string> RequestedPermissions => Permissions;

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

        // The grant ConnectSDK classifies as covering running-app reads. Added
        // because getForegroundAppInfo returns "401 insufficient permissions"
        // under READ_APP_STATUS alone. Adding it changes the permission set, so
        // an existing key keeps the old grant until a human re-pairs — see
        // PermissionsFingerprint above.
        "READ_RUNNING_APPS",

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

    // Declared AFTER Permissions on purpose: static field initialisers run in
    // textual order, so computing this above the array would hash a null.
    private static readonly string FingerprintValue = ComputeFingerprint(Permissions);
}

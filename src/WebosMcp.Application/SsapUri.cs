namespace WebosMcp.Application;

/// <summary>
/// The complete, closed set of SSAP endpoints this server will ever call.
/// There is deliberately no way for a caller to supply a URI — that is the
/// project's core safety boundary.
/// </summary>
internal static class SsapUri
{
    public const string GetSystemInfo = "ssap://system/getSystemInfo";
    public const string GetSoftwareInfo = "ssap://com.webos.service.update/getCurrentSWInformation";
    public const string GetPowerState = "ssap://com.webos.service.tvpower/power/getPowerState";
    public const string TurnOff = "ssap://system/turnOff";
    public const string TurnOffScreen = "ssap://com.webos.service.tvpower/power/turnOffScreen";
    public const string TurnOnScreen = "ssap://com.webos.service.tvpower/power/turnOnScreen";

    public const string GetForegroundApp = "ssap://com.webos.applicationManager/getForegroundAppInfo";
    public const string ListLaunchPoints = "ssap://com.webos.applicationManager/listLaunchPoints";
    public const string LaunchApp = "ssap://system.launcher/launch";
    public const string CloseApp = "ssap://system.launcher/close";
    public const string OpenBrowser = "ssap://system.launcher/open";

    public const string GetVolume = "ssap://audio/getVolume";
    public const string SetVolume = "ssap://audio/setVolume";
    public const string SetMute = "ssap://audio/setMute";
    public const string GetSoundOutput = "ssap://com.webos.service.apiadapter/audio/getSoundOutput";
    public const string ChangeSoundOutput = "ssap://com.webos.service.apiadapter/audio/changeSoundOutput";

    public const string MediaPlay = "ssap://media.controls/play";
    public const string MediaPause = "ssap://media.controls/pause";
    public const string MediaStop = "ssap://media.controls/stop";
    public const string MediaRewind = "ssap://media.controls/rewind";
    public const string MediaFastForward = "ssap://media.controls/fastForward";

    /// <summary>
    /// Undocumented by LG and model/firmware dependent. Returns an
    /// <c>imageUri</c> the TV serves the captured frame from; the TV is trusted
    /// for neither the URI nor the bytes behind it.
    /// </summary>
    public const string ExecuteOneShot = "ssap://tv/executeOneShot";

    public const string GetExternalInputList = "ssap://tv/getExternalInputList";
    public const string SwitchInput = "ssap://tv/switchInput";
    public const string GetCurrentChannel = "ssap://tv/getCurrentChannel";
    public const string GetChannelProgramInfo = "ssap://tv/getChannelProgramInfo";
    public const string ChannelUp = "ssap://tv/channelUp";
    public const string ChannelDown = "ssap://tv/channelDown";
    public const string OpenChannel = "ssap://tv/openChannel";

    public const string InsertText = "ssap://com.webos.service.ime/insertText";
    public const string DeleteCharacters = "ssap://com.webos.service.ime/deleteCharacters";
    public const string SendEnterKey = "ssap://com.webos.service.ime/sendEnterKey";

    public const string CreateToast = "ssap://system.notifications/createToast";

    public const string GetPointerInputSocket = "ssap://com.webos.service.networkinput/getPointerInputSocket";
}

using System.Text.Json;
using WebosMcp.Application;
using WebosMcp.Domain;
using WebosMcp.Tests.Fakes;
using Xunit;

namespace WebosMcp.Tests;

/// <summary>Tool call to SSAP payload: the URI chosen and the body actually sent.</summary>
public sealed class CommandMappingTests
{
    private static string? PayloadFor(FakeSsapConnection connection, string uri) =>
        connection.Calls.First(c => c.Kind == "request" && c.Target == uri).Payload;

    [Fact]
    public async Task Set_volume_sends_the_validated_level_to_the_audio_endpoint()
    {
        var harness = new TestHarness();

        await harness.Control.SetVolumeAsync(42, CancellationToken.None);

        Assert.Contains("ssap://audio/setVolume", harness.Connection.RequestUris);
        using var payload = JsonDocument.Parse(PayloadFor(harness.Connection, "ssap://audio/setVolume")!);
        Assert.Equal(42, payload.RootElement.GetProperty("volume").GetInt32());
    }

    [Fact]
    public async Task Set_mute_sends_the_boolean_state()
    {
        var harness = new TestHarness();

        await harness.Control.SetMuteAsync(true, CancellationToken.None);

        using var payload = JsonDocument.Parse(PayloadFor(harness.Connection, "ssap://audio/setMute")!);
        Assert.True(payload.RootElement.GetProperty("mute").GetBoolean());
    }

    [Theory]
    [InlineData(MediaCommand.Play, "ssap://media.controls/play")]
    [InlineData(MediaCommand.Pause, "ssap://media.controls/pause")]
    [InlineData(MediaCommand.Stop, "ssap://media.controls/stop")]
    [InlineData(MediaCommand.Rewind, "ssap://media.controls/rewind")]
    [InlineData(MediaCommand.FastForward, "ssap://media.controls/fastForward")]
    public async Task Media_commands_map_to_their_endpoints(MediaCommand command, string expected)
    {
        var harness = new TestHarness();

        await harness.Control.MediaControlAsync(command, CancellationToken.None);

        Assert.Contains(expected, harness.Connection.RequestUris);
    }

    [Fact]
    public async Task Launch_app_sends_the_app_id()
    {
        var harness = new TestHarness();

        await harness.Control.LaunchAppAsync("com.webos.app.browser", CancellationToken.None);

        using var payload = JsonDocument.Parse(PayloadFor(harness.Connection, "ssap://system.launcher/launch")!);
        Assert.Equal("com.webos.app.browser", payload.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Toast_sends_the_message_to_the_notifications_endpoint()
    {
        var harness = new TestHarness();

        await harness.Control.ShowToastAsync("dinner is ready", CancellationToken.None);

        using var payload = JsonDocument.Parse(
            PayloadFor(harness.Connection, "ssap://system.notifications/createToast")!);
        Assert.Equal("dinner is ready", payload.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Power_off_and_screen_toggles_map_to_distinct_endpoints()
    {
        var harness = new TestHarness();

        await harness.Control.PowerOffAsync(CancellationToken.None);
        await harness.Control.ScreenOffAsync(CancellationToken.None);
        await harness.Control.ScreenOnAsync(CancellationToken.None);

        Assert.Contains("ssap://system/turnOff", harness.Connection.RequestUris);
        Assert.Contains("ssap://com.webos.service.tvpower/power/turnOffScreen", harness.Connection.RequestUris);
        Assert.Contains("ssap://com.webos.service.tvpower/power/turnOnScreen", harness.Connection.RequestUris);
    }

    [Fact]
    public async Task Type_text_with_submit_sends_insert_then_enter_in_order()
    {
        var harness = new TestHarness();

        await harness.Control.TypeTextAsync("kitchen", replace: true, submit: true, CancellationToken.None);

        Assert.Equal(
            ["ssap://com.webos.service.ime/insertText", "ssap://com.webos.service.ime/sendEnterKey"],
            harness.Connection.RequestUris);

        using var payload = JsonDocument.Parse(
            PayloadFor(harness.Connection, "ssap://com.webos.service.ime/insertText")!);
        Assert.Equal("kitchen", payload.RootElement.GetProperty("text").GetString());
        Assert.True(payload.RootElement.GetProperty("replace").GetBoolean());
    }

    [Fact]
    public async Task Button_repeat_sends_exactly_that_many_presses()
    {
        var harness = new TestHarness();

        await harness.Control.SendButtonAsync(RemoteButton.Down, 3, CancellationToken.None);

        var presses = harness.Connection.Calls.Where(c => c.Kind == "button").ToList();
        Assert.Equal(3, presses.Count);
        Assert.All(presses, p => Assert.Equal("DOWN", p.Target));
    }

    [Fact]
    public async Task Pointer_operations_carry_their_deltas()
    {
        var harness = new TestHarness();

        await harness.Control.PointerMoveAsync(10, -20, drag: true, CancellationToken.None);
        await harness.Control.PointerClickAsync(CancellationToken.None);
        await harness.Control.PointerScrollAsync(0, 5, CancellationToken.None);

        Assert.Equal("10,-20,1", harness.Connection.Calls.First(c => c.Kind == "move").Target);
        Assert.Contains(harness.Connection.Calls, c => c.Kind == "click");
        Assert.Equal("0,5", harness.Connection.Calls.First(c => c.Kind == "scroll").Target);
    }

    [Fact]
    public async Task List_apps_projects_launch_points()
    {
        var connection = new FakeSsapConnection();
        connection.Respond(
            "ssap://com.webos.applicationManager/listLaunchPoints",
            """
            {"returnValue":true,"launchPoints":[
              {"id":"netflix","title":"Netflix","systemApp":false},
              {"id":"com.webos.app.browser","title":"Web Browser","systemApp":true},
              {"title":"missing id"}
            ]}
            """);

        var harness = new TestHarness(connection);
        var apps = await harness.Control.ListAppsAsync(CancellationToken.None);

        // The entry with no id is dropped rather than surfaced as a blank app.
        Assert.Equal(2, apps.Count);
        Assert.Equal("netflix", apps[0].Id);
        Assert.True(apps[1].System);
    }

    [Fact]
    public async Task External_inputs_are_projected_with_their_connected_state()
    {
        var connection = new FakeSsapConnection();
        connection.Respond(
            "ssap://tv/getExternalInputList",
            """
            {"returnValue":true,"devices":[
              {"id":"HDMI_1","label":"Console","connected":true},
              {"id":"HDMI_2","label":"Spare","connected":false}
            ]}
            """);

        var harness = new TestHarness(connection);
        var inputs = await harness.Control.ListInputsAsync(CancellationToken.None);

        Assert.Equal(2, inputs.Count);
        Assert.True(inputs[0].Connected);
        Assert.Equal("Spare", inputs[1].Label);
    }

    [Fact]
    public async Task Switch_input_validates_against_what_the_tv_reports()
    {
        var connection = new FakeSsapConnection();
        connection.Respond(
            "ssap://tv/getExternalInputList",
            """{"returnValue":true,"devices":[{"id":"HDMI_1","label":"Console","connected":true}]}""");

        var harness = new TestHarness(connection);

        var ex = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.SwitchInputAsync("HDMI_9", CancellationToken.None));

        Assert.Equal(TvErrorCode.InvalidInput, ex.Code);
        Assert.DoesNotContain("ssap://tv/switchInput", harness.Connection.RequestUris);
    }

    [Fact]
    public async Task Switch_input_sends_the_matched_id()
    {
        var connection = new FakeSsapConnection();
        connection.Respond(
            "ssap://tv/getExternalInputList",
            """{"returnValue":true,"devices":[{"id":"HDMI_1","label":"Console","connected":true}]}""");

        var harness = new TestHarness(connection);
        await harness.Control.SwitchInputAsync("hdmi_1", CancellationToken.None);

        using var payload = JsonDocument.Parse(PayloadFor(harness.Connection, "ssap://tv/switchInput")!);
        Assert.Equal("HDMI_1", payload.RootElement.GetProperty("inputId").GetString());
    }

    [Fact]
    public async Task Sound_output_is_validated_against_the_reported_list()
    {
        var connection = new FakeSsapConnection();
        connection.Respond(
            "ssap://com.webos.service.apiadapter/audio/getSoundOutput",
            """{"returnValue":true,"soundOutputList":["tv_speaker","external_optical"]}""");

        var harness = new TestHarness(connection);

        var ex = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.SetSoundOutputAsync("headphones", CancellationToken.None));

        Assert.Equal(TvErrorCode.InvalidInput, ex.Code);
        Assert.DoesNotContain(
            "ssap://com.webos.service.apiadapter/audio/changeSoundOutput",
            harness.Connection.RequestUris);
    }

    [Theory]
    [InlineData("Active", null, PowerState.Active)]
    [InlineData("Active Standby", null, PowerState.Standby)]
    [InlineData("Screen Off", null, PowerState.ScreenOff)]
    [InlineData("Active", "Screen Off", PowerState.ScreenOff)]
    [InlineData("Suspend", null, PowerState.Standby)]
    [InlineData("Power Off", null, PowerState.Standby)]
    [InlineData("wat", null, PowerState.Unknown)]
    public void Power_state_strings_map_to_normalised_states(string state, string? processing, PowerState expected) =>
        Assert.Equal(expected, TvControlService.MapPowerState(state, processing));

    [Fact]
    public void Volume_payloads_are_read_in_both_the_flat_and_nested_shapes()
    {
        using var flat = JsonDocument.Parse("""{"volume":31,"mute":true,"soundOutput":"tv_speaker"}""");
        var flatState = TvControlService.ReadVolumeState(flat.RootElement);
        Assert.Equal(31, flatState.Volume);
        Assert.True(flatState.Muted);

        using var nested = JsonDocument.Parse(
            """{"volumeStatus":{"volume":12,"muteStatus":false,"soundOutput":"external_optical","volumeMax":100}}""");
        var nestedState = TvControlService.ReadVolumeState(nested.RootElement);
        Assert.Equal(12, nestedState.Volume);
        Assert.False(nestedState.Muted);
        Assert.Equal("external_optical", nestedState.SoundOutput);
        Assert.Equal(100, nestedState.MaxVolume);
    }
}

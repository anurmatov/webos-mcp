using System.Text.Json;
using WebosMcp.Application;
using WebosMcp.Domain;
using WebosMcp.Tests.Fakes;
using Xunit;

namespace WebosMcp.Tests;

/// <summary>
/// Content launching, after physical testing showed the old implementation
/// reporting success while the TV sat on the home screen. The rule these
/// tests enforce: an accepted launch is not playback, and nothing is reported
/// as success without an observed post-condition.
/// </summary>
public sealed class ContentPathTests
{
    /// <summary>Makes the TV report YouTube as the foreground app.</summary>
    private static void ForegroundIsYouTube(FakeSsapConnection connection) =>
        connection.Respond(
            "ssap://com.webos.applicationManager/getForegroundAppInfo",
            """{"returnValue":true,"appId":"youtube.leanback.v4"}""");

    /// <summary>Makes the TV report it is sitting on the home screen.</summary>
    private static void ForegroundIsHome(FakeSsapConnection connection) =>
        connection.Respond(
            "ssap://com.webos.applicationManager/getForegroundAppInfo",
            """{"returnValue":true,"appId":"com.webos.app.home"}""");

    // ------------------------------------------------------------- open url

    [Fact]
    public async Task Open_url_uses_the_direct_launcher_deep_link()
    {
        var harness = new TestHarness();

        var result = await harness.Control.OpenUrlAsync("https://example.com/page", CancellationToken.None);

        Assert.Equal(ActionPath.DeepLink, result.Path);

        var call = harness.Connection.Calls.Single(c => c.Target == "ssap://system.launcher/open");
        using var payload = JsonDocument.Parse(call.Payload!);
        Assert.Equal("https://example.com/page", payload.RootElement.GetProperty("target").GetString());
    }

    // -------------------------------------------------------- youtube play

    [Fact]
    public async Task Play_launches_over_dial_and_confirms_the_app_reached_the_foreground()
    {
        var connection = new FakeSsapConnection();
        ForegroundIsYouTube(connection);
        var harness = new TestHarness(connection);

        var result = await harness.Control.PlayYouTubeAsync("dQw4w9WgXcQ", CancellationToken.None);

        Assert.Equal(ActionPath.Dial, result.Path);
        Assert.Equal(1, harness.Dial.LaunchCount);
        Assert.Equal(["v=dQw4w9WgXcQ"], harness.Dial.LaunchPayloads);

        // The success message names the evidence, not just the request.
        Assert.Contains("confirmed", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("foreground", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Play_accepts_a_full_url_and_launches_the_bare_video_id()
    {
        var connection = new FakeSsapConnection();
        ForegroundIsYouTube(connection);
        var harness = new TestHarness(connection);

        await harness.Control.PlayYouTubeAsync(
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ", CancellationToken.None);

        Assert.Equal(["v=dQw4w9WgXcQ"], harness.Dial.LaunchPayloads);
    }

    [Fact]
    public async Task Play_NEVER_reports_success_when_the_tv_stays_on_home()
    {
        // The exact defect physical testing found: DIAL accepts the launch,
        // the TV does not switch app, and the old code called that success.
        var connection = new FakeSsapConnection();
        ForegroundIsHome(connection);
        var harness = new TestHarness(connection);

        var ex = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.PlayYouTubeAsync("dQw4w9WgXcQ", CancellationToken.None));

        Assert.Equal(TvErrorCode.TvError, ex.Code);
        Assert.Contains("did not reach the foreground", ex.Message, StringComparison.OrdinalIgnoreCase);

        // It really did try — this is a verification failure, not a skipped launch.
        Assert.Equal(1, harness.Dial.LaunchCount);
    }

    [Fact]
    public async Task Play_returns_UNSUPPORTED_when_the_tv_has_no_dial_endpoint()
    {
        var harness = new TestHarness();
        harness.Dial.ApplicationUrl = null;

        var ex = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.PlayYouTubeAsync("dQw4w9WgXcQ", CancellationToken.None));

        Assert.Equal(TvErrorCode.TvUnsupportedCapability, ex.Code);
        Assert.Equal("TV_UNSUPPORTED_CAPABILITY", ex.Code.ToWireCode());
        Assert.Equal(0, harness.Dial.LaunchCount);
    }

    [Fact]
    public async Task Play_returns_UNSUPPORTED_when_youtube_is_not_installed()
    {
        var harness = new TestHarness();
        harness.Dial.AppStatus = null;   // DIAL answers 404 for a missing app

        var ex = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.PlayYouTubeAsync("dQw4w9WgXcQ", CancellationToken.None));

        Assert.Equal(TvErrorCode.TvUnsupportedCapability, ex.Code);
        Assert.Equal(0, harness.Dial.LaunchCount);
    }

    [Fact]
    public async Task Play_returns_UNSUPPORTED_when_the_app_is_only_installable()
    {
        var harness = new TestHarness();
        harness.Dial.AppStatus = new DialAppStatus("YouTube", "installable=https://example.com", Installed: false);

        var ex = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.PlayYouTubeAsync("dQw4w9WgXcQ", CancellationToken.None));

        Assert.Equal(TvErrorCode.TvUnsupportedCapability, ex.Code);
    }

    [Fact]
    public async Task Play_reports_failure_when_the_tv_rejects_the_dial_launch()
    {
        var harness = new TestHarness();
        harness.Dial.LaunchAccepted = false;

        var ex = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.PlayYouTubeAsync("dQw4w9WgXcQ", CancellationToken.None));

        Assert.Equal(TvErrorCode.TvError, ex.Code);
        Assert.Contains("rejected", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Play_does_not_use_the_ssap_launcher_at_all()
    {
        var connection = new FakeSsapConnection();
        ForegroundIsYouTube(connection);
        var harness = new TestHarness(connection);

        await harness.Control.PlayYouTubeAsync("dQw4w9WgXcQ", CancellationToken.None);

        // The SSAP launcher is what produced the false success; the only SSAP
        // traffic here should be the foreground-app confirmation.
        Assert.DoesNotContain("ssap://system.launcher/launch", harness.Connection.RequestUris);
        Assert.Contains(
            "ssap://com.webos.applicationManager/getForegroundAppInfo", harness.Connection.RequestUris);
    }

    [Fact]
    public async Task Play_rejects_a_malformed_video_reference_before_touching_dial()
    {
        // Note: a bare 11-character string is a VALID id by shape, hyphens and
        // all — so an invalid case has to be something the pattern really
        // rejects, such as a non-YouTube host.
        var harness = new TestHarness();

        var ex = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.PlayYouTubeAsync("https://example.com/watch?v=dQw4w9WgXcQ", CancellationToken.None));

        Assert.Equal(TvErrorCode.InvalidInput, ex.Code);
        Assert.Equal(0, harness.Dial.ResolveCount);
    }

    [Fact]
    public async Task Foreground_confirmation_is_bounded_and_does_not_spin()
    {
        var connection = new FakeSsapConnection();
        ForegroundIsHome(connection);
        var harness = new TestHarness(connection, o =>
        {
            o.LaunchVerifyTimeoutSeconds = 6;
            o.LaunchPollIntervalSeconds = 2;
        });

        await Assert.ThrowsAsync<TvException>(
            () => harness.Control.PlayYouTubeAsync("dQw4w9WgXcQ", CancellationToken.None));

        // 6s budget at a 2s interval is three polls.
        Assert.Equal(3, harness.Delay.Count);
    }

    // ------------------------------------------------------ youtube search

    [Fact]
    public async Task Search_reports_UNSUPPORTED_rather_than_a_fake_success()
    {
        var harness = new TestHarness();

        var ex = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.SearchYouTubeAsync("cooking pasta", CancellationToken.None));

        Assert.Equal(TvErrorCode.TvUnsupportedCapability, ex.Code);
        Assert.Contains("keyboard", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The removed fallback must not come back: nothing was typed, no
        // button was pressed, and no app was launched.
        Assert.Empty(harness.Connection.Calls);
        Assert.Equal(0, harness.Dial.LaunchCount);
    }

    [Fact]
    public async Task Search_still_validates_its_input()
    {
        var harness = new TestHarness();

        var ex = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.SearchYouTubeAsync("   ", CancellationToken.None));

        // A bad query is bad input, not "unsupported" — the two must not blur.
        Assert.Equal(TvErrorCode.InvalidInput, ex.Code);
    }

    // ------------------------------------------------- custom keyboard apps

    [Fact]
    public async Task Type_text_refuses_when_a_custom_keyboard_app_is_in_the_foreground()
    {
        var connection = new FakeSsapConnection();
        ForegroundIsYouTube(connection);
        var harness = new TestHarness(connection);

        var ex = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.TypeTextAsync("pasta", false, true, CancellationToken.None));

        Assert.Equal(TvErrorCode.TvUnsupportedCapability, ex.Code);
        Assert.Contains("custom on-screen keyboard", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Crucially: it did NOT type and then claim success.
        Assert.DoesNotContain("ssap://com.webos.service.ime/insertText", harness.Connection.RequestUris);
    }

    [Fact]
    public async Task Type_text_still_works_in_an_ordinary_app()
    {
        var connection = new FakeSsapConnection();
        connection.Respond(
            "ssap://com.webos.applicationManager/getForegroundAppInfo",
            """{"returnValue":true,"appId":"com.webos.app.browser"}""");
        var harness = new TestHarness(connection);

        await harness.Control.TypeTextAsync("hello", false, true, CancellationToken.None);

        Assert.Contains("ssap://com.webos.service.ime/insertText", harness.Connection.RequestUris);
        Assert.Contains("ssap://com.webos.service.ime/sendEnterKey", harness.Connection.RequestUris);
    }
}

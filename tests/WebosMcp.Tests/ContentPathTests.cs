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
    //
    // Play now runs over Lounge. DIAL only supplies the receiver's screen id: it
    // cannot select a video in a running session and cannot report which video is
    // playing, which is how three earlier revisions reported success over the wrong
    // video. Full Lounge coverage lives in YouTubeLoungeTests.

    [Fact]
    public async Task Play_loads_the_video_over_lounge_and_confirms_the_receiver_reported_it()
    {
        var harness = new TestHarness();
        harness.Lounge.Session!.Reports.Add(new LoungeReceiverState("dQw4w9WgXcQ", LoungePlayerState.Playing));

        var result = await harness.Control.PlayYouTubeAsync("dQw4w9WgXcQ", CancellationToken.None);

        Assert.Equal(ActionPath.Lounge, result.Path);
        Assert.True(result.ExactVideoConfirmed);
        Assert.Equal("dQw4w9WgXcQ", result.ObservedVideoId);
    }

    [Fact]
    public async Task Play_accepts_a_full_url_and_sends_the_bare_video_id()
    {
        var harness = new TestHarness();
        harness.Lounge.Session!.Reports.Add(new LoungeReceiverState("dQw4w9WgXcQ", LoungePlayerState.Playing));

        await harness.Control.PlayYouTubeAsync(
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ", CancellationToken.None);

        var sent = Assert.Single(harness.Lounge.Session.Sent);
        Assert.Equal("setPlaylist", sent.Command);
        Assert.Equal("dQw4w9WgXcQ", sent.Parameters["videoId"]);
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
    public async Task Play_does_not_use_the_ssap_launcher_at_all()
    {
        var harness = new TestHarness();
        harness.Lounge.Session!.Reports.Add(new LoungeReceiverState("dQw4w9WgXcQ", LoungePlayerState.Playing));

        await harness.Control.PlayYouTubeAsync("dQw4w9WgXcQ", CancellationToken.None);

        // The SSAP launcher is what produced the first false success.
        Assert.DoesNotContain("ssap://system.launcher/launch", harness.Connection.RequestUris);
    }

    [Fact]
    public async Task Play_rejects_a_malformed_video_reference_before_touching_the_tv()
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

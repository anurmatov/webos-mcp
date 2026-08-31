using WebosMcp.Application;
using WebosMcp.Domain;
using WebosMcp.Infrastructure;
using WebosMcp.Tests.Fakes;
using Xunit;

namespace WebosMcp.Tests;

/// <summary>
/// The already-running case, and why it is its own class of bug.
///
/// Physical testing: tv_youtube_play was called with a specific video, the TV
/// accepted the DIAL launch, YouTube stayed in the foreground — and kept playing
/// the PREVIOUS video. It was reported as success, because the only post-condition
/// checked was "is YouTube the foreground app", which was already true before the
/// call. The check could not fail, so it was not evidence of anything.
///
/// A DIAL launch aimed at a running app does not change what it is playing. The
/// only way to make a requested video take effect is a cold start, so a running
/// instance must be stopped first — and where that is impossible the answer is
/// TV_UNSUPPORTED_CAPABILITY, never a launch reported as playback.
/// </summary>
public sealed class YouTubeExactVideoTests
{
    private const string Video = "dQw4w9WgXcQ";

    private static void ForegroundIsYouTube(FakeSsapConnection connection) =>
        connection.Respond(
            "ssap://com.webos.applicationManager/getForegroundAppInfo",
            """{"returnValue":true,"appId":"youtube.leanback.v4"}""");

    private static DialAppStatus Running(bool allowStop = true, string? runLink = "run") =>
        new("YouTube", "running", Installed: true, AllowStop: allowStop, RunLink: runLink);

    private static TestHarness RunningYouTube(
        bool allowStop = true,
        string? runLink = "run",
        Action<FakeDialClient>? configure = null)
    {
        var connection = new FakeSsapConnection();
        ForegroundIsYouTube(connection);

        var harness = new TestHarness(connection);
        harness.Dial.AppStatus = Running(allowStop, runLink);
        configure?.Invoke(harness.Dial);

        return harness;
    }

    // ---- the regression --------------------------------------------------

    [Fact]
    public async Task A_running_session_is_stopped_and_cold_started_so_the_video_takes_effect()
    {
        var harness = RunningYouTube();

        var result = await harness.Control.PlayYouTubeAsync(Video, CancellationToken.None);

        Assert.Equal(1, harness.Dial.StopCount);
        Assert.Equal(1, harness.Dial.LaunchCount);
        Assert.Equal([$"v={Video}"], harness.Dial.LaunchPayloads);
        Assert.True(result.ColdStarted);
    }

    [Fact]
    public async Task A_running_session_that_cannot_be_stopped_is_unsupported_not_success()
    {
        // THE bug: YouTube is already foreground, so the old foreground check passed
        // instantly while the previous video kept playing. With no way to cold start,
        // the only honest answer is that this TV cannot honour the request.
        var harness = RunningYouTube(allowStop: false);

        var error = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.PlayYouTubeAsync(Video, CancellationToken.None));

        Assert.Equal(TvErrorCode.TvUnsupportedCapability, error.Code);

        // And it must not have touched the TV: launching here is precisely what
        // produced the wrong-video false success.
        Assert.Equal(0, harness.Dial.LaunchCount);
        Assert.Equal(0, harness.Dial.StopCount);
    }

    [Fact]
    public async Task A_running_session_with_no_instance_link_is_unsupported()
    {
        // allowStop is advertised but there is no address to send the stop to.
        var harness = RunningYouTube(runLink: null);

        var error = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.PlayYouTubeAsync(Video, CancellationToken.None));

        Assert.Equal(TvErrorCode.TvUnsupportedCapability, error.Code);
        Assert.Equal(0, harness.Dial.LaunchCount);
    }

    [Fact]
    public async Task A_refused_stop_is_unsupported_and_never_launches_over_the_old_session()
    {
        var harness = RunningYouTube(configure: d => d.StopAccepted = false);

        var error = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.PlayYouTubeAsync(Video, CancellationToken.None));

        Assert.Equal(TvErrorCode.TvUnsupportedCapability, error.Code);
        Assert.Equal(1, harness.Dial.StopCount);
        Assert.Equal(0, harness.Dial.LaunchCount);
    }

    [Fact]
    public async Task A_stop_that_is_accepted_but_never_takes_effect_is_unsupported()
    {
        // Accepting the stop is not stopping, the same distinction as accepting a
        // launch not being playback.
        var harness = RunningYouTube(configure: d => d.StopLeavesAppRunning = true);

        var error = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.PlayYouTubeAsync(Video, CancellationToken.None));

        Assert.Equal(TvErrorCode.TvUnsupportedCapability, error.Code);
        Assert.Equal(0, harness.Dial.LaunchCount);
    }

    // ---- the not-running path is unchanged --------------------------------

    [Fact]
    public async Task A_stopped_app_is_launched_directly_with_no_stop()
    {
        var connection = new FakeSsapConnection();
        ForegroundIsYouTube(connection);
        var harness = new TestHarness(connection);   // fake defaults to a stopped app

        var result = await harness.Control.PlayYouTubeAsync(Video, CancellationToken.None);

        Assert.Equal(0, harness.Dial.StopCount);
        Assert.Equal(1, harness.Dial.LaunchCount);
        Assert.False(result.ColdStarted);
    }

    // ---- exactness is never claimed ----------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Success_never_claims_the_exact_video_was_confirmed(bool alreadyRunning)
    {
        // DIAL cannot report which video is on screen. Saying so is the difference
        // between an honest result and the one that shipped.
        var harness = alreadyRunning ? RunningYouTube() : new TestHarness(WithYouTubeForeground());

        var result = await harness.Control.PlayYouTubeAsync(Video, CancellationToken.None);

        Assert.False(result.ExactVideoConfirmed);
        Assert.Contains("not a read-back confirmation", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static FakeSsapConnection WithYouTubeForeground()
    {
        var connection = new FakeSsapConnection();
        ForegroundIsYouTube(connection);
        return connection;
    }

    // ---- parsing the fields the decision depends on -------------------------

    [Fact]
    public void Allow_stop_and_the_run_link_are_parsed_from_the_status_document()
    {
        var status = DialClient.ParseAppStatus("YouTube",
            """
            <service xmlns="urn:dial-multiscreen-org:schemas:dial">
              <name>YouTube</name>
              <options allowStop="true"/>
              <state>running</state>
              <link rel="run" href="run"/>
            </service>
            """);

        Assert.True(status!.AllowStop);
        Assert.Equal("run", status.RunLink);
        Assert.True(status.CanStop);
    }

    [Fact]
    public void A_status_without_allow_stop_cannot_be_stopped()
    {
        var status = DialClient.ParseAppStatus("YouTube",
            """
            <service xmlns="urn:dial-multiscreen-org:schemas:dial">
              <name>YouTube</name><options allowStop="false"/><state>running</state>
              <link rel="run" href="run"/>
            </service>
            """);

        Assert.False(status!.AllowStop);
        Assert.False(status.CanStop);
    }

    [Fact]
    public void A_status_with_no_run_link_cannot_be_stopped()
    {
        var status = DialClient.ParseAppStatus("YouTube",
            """<service xmlns="urn:dial-multiscreen-org:schemas:dial"><name>YouTube</name><options allowStop="true"/><state>running</state></service>""");

        Assert.True(status!.AllowStop);
        Assert.Null(status.RunLink);
        Assert.False(status.CanStop);
    }

    [Theory]
    [InlineData("run", "http://192.0.2.10:2038/apps/YouTube/run")]
    [InlineData("/apps/YouTube/run", "http://192.0.2.10:2038/apps/YouTube/run")]
    [InlineData("http://192.0.2.10:2038/apps/YouTube/run", "http://192.0.2.10:2038/apps/YouTube/run")]
    public void The_instance_url_handles_relative_and_absolute_run_links(string runLink, string expected)
    {
        var url = DialClient.InstanceUrl(new Uri("http://192.0.2.10:2038/apps/"), "YouTube", runLink);

        Assert.Equal(expected, url.AbsoluteUri);
    }
}

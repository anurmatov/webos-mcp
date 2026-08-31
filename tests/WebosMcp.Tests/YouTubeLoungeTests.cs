using WebosMcp.Application;
using WebosMcp.Domain;
using WebosMcp.Infrastructure;
using WebosMcp.Tests.Fakes;
using Xunit;

namespace WebosMcp.Tests;

/// <summary>
/// YouTube control over the Lounge protocol.
///
/// The history this closes: three revisions in a row reported success while the TV
/// played the wrong video. DIAL cannot select a video in a running session — the
/// launch is accepted and ignored — and it cannot report which video is playing, so
/// no DIAL-only check could ever have caught it. Lounge does both, and physical
/// testing confirmed it loads the exact video into a live session.
///
/// The rules these tests hold:
///   1. Success requires the RECEIVER to report the requested video playing.
///   2. YouTube is never restarted to change video (that lands on the profile picker).
///   3. A command with no confirming event is reported observed=false, not success.
/// </summary>
public sealed class YouTubeLoungeTests
{
    private const string Video = "dQw4w9WgXcQ";
    private const string Other = "aBcDeFgHiJk";

    private static TestHarness Ready(params LoungeReceiverState[] reports)
    {
        var harness = new TestHarness();
        harness.Lounge.Session!.Reports.AddRange(reports);
        return harness;
    }

    private static LoungeReceiverState Playing(string videoId) =>
        new(videoId, LoungePlayerState.Playing);

    // ---- exact video, observed ---------------------------------------------

    [Fact]
    public async Task Play_sends_setPlaylist_with_the_requested_video()
    {
        var harness = Ready(Playing(Video));

        await harness.Control.PlayYouTubeAsync(Video, CancellationToken.None);

        var sent = Assert.Single(harness.Lounge.Session!.Sent);
        Assert.Equal("setPlaylist", sent.Command);
        Assert.Equal(Video, sent.Parameters["videoId"]);
    }

    [Fact]
    public async Task Play_succeeds_only_when_the_receiver_reports_THAT_video_playing()
    {
        var harness = Ready(Playing(Video));

        var result = await harness.Control.PlayYouTubeAsync(Video, CancellationToken.None);

        Assert.True(result.ExactVideoConfirmed);
        Assert.Equal(Video, result.ObservedVideoId);
        Assert.Equal("Playing", result.ObservedState);
    }

    [Fact]
    public async Task A_different_video_playing_is_NOT_success()
    {
        // The exact physical failure: the receiver keeps playing what it had. A
        // report naming another video must never satisfy this request.
        var harness = Ready(Playing(Other));

        var error = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.PlayYouTubeAsync(Video, CancellationToken.None));

        Assert.Equal(TvErrorCode.TvError, error.Code);
        Assert.Contains("never reported it playing", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_right_video_merely_cued_or_paused_is_NOT_success()
    {
        // Loaded is not playing. Accepting a cued state would reintroduce exactly
        // the "it's on screen so call it done" reasoning this whole thread is about.
        var harness = Ready(
            new LoungeReceiverState(Video, LoungePlayerState.Cued),
            new LoungeReceiverState(Video, LoungePlayerState.Paused));

        await Assert.ThrowsAsync<TvException>(
            () => harness.Control.PlayYouTubeAsync(Video, CancellationToken.None));
    }

    [Fact]
    public async Task A_receiver_that_reports_nothing_is_a_failure_not_a_success()
    {
        var harness = Ready();   // silent receiver

        var error = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.PlayYouTubeAsync(Video, CancellationToken.None));

        Assert.Equal(TvErrorCode.TvError, error.Code);
    }

    [Fact]
    public async Task Intermediate_reports_are_tolerated_before_the_confirming_one()
    {
        // Buffering, then the wrong video, then the right one: still a success.
        var harness = Ready(
            new LoungeReceiverState(Video, LoungePlayerState.Buffering),
            Playing(Other),
            Playing(Video));

        var result = await harness.Control.PlayYouTubeAsync(Video, CancellationToken.None);

        Assert.True(result.ExactVideoConfirmed);
    }

    // ---- never restart to change video -------------------------------------

    [Fact]
    public async Task A_running_receiver_is_never_relaunched_to_change_video()
    {
        // On the physical TV a stop/relaunch lands on the account picker, which is
        // worse than the bug it would work around.
        var harness = Ready(Playing(Video));

        await harness.Control.PlayYouTubeAsync(Video, CancellationToken.None);

        Assert.Equal(0, harness.Dial.LaunchCount);
    }

    [Fact]
    public async Task A_stopped_receiver_is_launched_once_so_there_is_something_to_control()
    {
        // Launching a stopped app is not a restart-to-select-video.
        var harness = new TestHarness();
        harness.Dial.AppStatus = new DialAppStatus("YouTube", "stopped", Installed: true, ScreenId: "screen-1");
        harness.Lounge.Session!.Reports.Add(Playing(Video));

        // The receiver reports running on the next status read.
        harness.Dial.StatusAfterLaunch =
            new DialAppStatus("YouTube", "running", Installed: true, ScreenId: "screen-1");

        var result = await harness.Control.PlayYouTubeAsync(Video, CancellationToken.None);

        Assert.Equal(1, harness.Dial.LaunchCount);
        Assert.True(result.ExactVideoConfirmed);
    }

    // ---- receiver that cannot be controlled --------------------------------

    [Fact]
    public async Task No_screen_id_is_unsupported_rather_than_an_unverifiable_launch()
    {
        var harness = new TestHarness();
        harness.Dial.AppStatus = new DialAppStatus("YouTube", "running", Installed: true, ScreenId: null);

        var error = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.PlayYouTubeAsync(Video, CancellationToken.None));

        Assert.Equal(TvErrorCode.TvUnsupportedCapability, error.Code);
        Assert.Contains("screen id", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_refused_lounge_session_is_unsupported()
    {
        var harness = new TestHarness();
        harness.Lounge.Session = null;

        var error = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.PlayYouTubeAsync(Video, CancellationToken.None));

        Assert.Equal(TvErrorCode.TvUnsupportedCapability, error.Code);
    }

    [Fact]
    public async Task The_screen_id_from_dial_is_what_the_session_connects_to()
    {
        var harness = Ready(Playing(Video));

        await harness.Control.PlayYouTubeAsync(Video, CancellationToken.None);

        Assert.Equal(["screen-1"], harness.Lounge.ConnectedScreenIds);
    }

    // ---- transport controls, judged on observation -------------------------

    [Fact]
    public async Task Pause_succeeds_only_once_the_receiver_reports_paused()
    {
        var harness = Ready(new LoungeReceiverState(Video, LoungePlayerState.Paused));

        var result = await harness.Control.YouTubePauseAsync(CancellationToken.None);

        Assert.True(result.Observed);
        Assert.Equal("pause", Assert.Single(harness.Lounge.Session!.Sent).Command);
        Assert.Equal("Paused", result.ObservedState);
    }

    [Fact]
    public async Task Pause_that_the_receiver_never_confirms_is_a_failure()
    {
        var harness = Ready(Playing(Video));   // still playing — never paused

        await Assert.ThrowsAsync<TvException>(
            () => harness.Control.YouTubePauseAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Resume_succeeds_only_once_the_receiver_reports_playing()
    {
        var harness = Ready(Playing(Video));

        var result = await harness.Control.YouTubeResumeAsync(CancellationToken.None);

        Assert.True(result.Observed);
        Assert.Equal("play", Assert.Single(harness.Lounge.Session!.Sent).Command);
    }

    [Fact]
    public async Task Seek_sends_the_position_and_reports_the_observed_time()
    {
        var harness = Ready(new LoungeReceiverState(Video, LoungePlayerState.Playing, CurrentTime: 42));

        var result = await harness.Control.YouTubeSeekAsync(42, CancellationToken.None);

        var sent = Assert.Single(harness.Lounge.Session!.Sent);
        Assert.Equal("seekTo", sent.Command);
        Assert.Equal("42", sent.Parameters["newTime"]);
        Assert.Equal(42, result.ObservedCurrentTime);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public async Task Seek_rejects_a_nonsensical_position_before_touching_the_tv(double seconds)
    {
        var harness = new TestHarness();

        var error = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.YouTubeSeekAsync(seconds, CancellationToken.None));

        Assert.Equal(TvErrorCode.InvalidInput, error.Code);
        Assert.Empty(harness.Lounge.ConnectedScreenIds);
    }

    [Fact]
    public async Task Next_and_previous_report_the_video_the_receiver_moved_to()
    {
        var harness = Ready(Playing(Other));

        var result = await harness.Control.YouTubeNextAsync(CancellationToken.None);

        Assert.Equal("next", Assert.Single(harness.Lounge.Session!.Sent).Command);
        Assert.Equal(Other, result.ObservedVideoId);
    }

    [Fact]
    public async Task Receiver_volume_succeeds_only_once_the_receiver_reports_it()
    {
        var harness = Ready(new LoungeReceiverState(Volume: 30));

        var result = await harness.Control.YouTubeSetVolumeAsync(30, CancellationToken.None);

        Assert.True(result.Observed);
        Assert.Equal(30, result.ObservedVolume);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task Receiver_volume_outside_0_to_100_is_rejected(int volume)
    {
        var harness = new TestHarness();

        var error = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.YouTubeSetVolumeAsync(volume, CancellationToken.None));

        Assert.Equal(TvErrorCode.InvalidInput, error.Code);
    }

    [Fact]
    public async Task Autoplay_succeeds_only_once_the_receiver_reports_the_new_mode()
    {
        var harness = Ready(new LoungeReceiverState(AutoplayEnabled: true));

        var result = await harness.Control.YouTubeSetAutoplayAsync(true, CancellationToken.None);

        Assert.True(result.Observed);
        Assert.True(result.ObservedAutoplayEnabled);
        Assert.Equal("ENABLED", Assert.Single(harness.Lounge.Session!.Sent).Parameters["autoplayMode"]);
    }

    [Fact]
    public async Task Autoplay_reporting_the_OPPOSITE_mode_is_not_success()
    {
        var harness = Ready(new LoungeReceiverState(AutoplayEnabled: false));

        await Assert.ThrowsAsync<TvException>(
            () => harness.Control.YouTubeSetAutoplayAsync(true, CancellationToken.None));
    }

    // ---- accepted-but-not-observed, stated as such -------------------------

    [Fact]
    public async Task Playback_speed_reports_observed_false_because_the_receiver_announces_nothing()
    {
        // Physical probing confirmed the speed change happens, but the receiver
        // emits no speed event — so the tool cannot observe it and must not claim to.
        var harness = Ready();

        var result = await harness.Control.YouTubeSetPlaybackSpeedAsync(1.5, CancellationToken.None);

        Assert.False(result.Observed);
        Assert.Contains("NOT observed", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("1.5", Assert.Single(harness.Lounge.Session!.Sent).Parameters["playbackSpeed"]);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(3.0)]
    public async Task Playback_speed_outside_the_supported_range_is_rejected(double speed)
    {
        var harness = new TestHarness();

        var error = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.YouTubeSetPlaybackSpeedAsync(speed, CancellationToken.None));

        Assert.Equal(TvErrorCode.InvalidInput, error.Code);
    }

    [Fact]
    public async Task Queue_add_uses_addVideo_and_reports_observed_false()
    {
        // Comma-separated ids on setPlaylist did NOT build a reliable queue on the
        // physical receiver; sequential addVideo did. Do not batch this.
        var harness = Ready();

        var result = await harness.Control.YouTubeQueueAddAsync(Other, CancellationToken.None);

        var sent = Assert.Single(harness.Lounge.Session!.Sent);
        Assert.Equal("addVideo", sent.Command);
        Assert.Equal(Other, sent.Parameters["videoId"]);
        Assert.False(result.Observed);
    }

    // ---- control commands never start or restart the app -------------------

    [Fact]
    public async Task Controls_refuse_when_youtube_is_not_running_rather_than_launching_it()
    {
        var harness = new TestHarness();
        harness.Dial.AppStatus = new DialAppStatus("YouTube", "stopped", Installed: true, ScreenId: "screen-1");

        var error = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.YouTubePauseAsync(CancellationToken.None));

        Assert.Equal(TvErrorCode.TvUnsupportedCapability, error.Code);
        Assert.Equal(0, harness.Dial.LaunchCount);
    }

    // ---- now playing is pure observation -----------------------------------

    [Fact]
    public async Task Now_playing_reports_what_the_receiver_says()
    {
        var harness = Ready(new LoungeReceiverState(Video, LoungePlayerState.Playing, CurrentTime: 12.5));

        var result = await harness.Control.YouTubeNowPlayingAsync(CancellationToken.None);

        Assert.True(result.Observed);
        Assert.Equal(Video, result.ObservedVideoId);
        Assert.Equal(12.5, result.ObservedCurrentTime);
    }

    [Fact]
    public async Task Now_playing_on_a_silent_receiver_reports_nothing_observed()
    {
        // A read must not invent state, and must not fail loudly either — it simply
        // has nothing to report.
        var harness = Ready();

        var result = await harness.Control.YouTubeNowPlayingAsync(CancellationToken.None);

        Assert.False(result.Observed);
        Assert.Null(result.ObservedVideoId);
    }

    // ---- protocol parsing ---------------------------------------------------

    [Fact]
    public void The_lounge_token_is_read_from_the_screens_batch()
    {
        Assert.Equal(
            "token-abc",
            LoungeClient.ParseLoungeToken("""{"screens":[{"screenId":"s","loungeToken":"token-abc"}]}"""));
    }

    [Theory]
    [InlineData("""{"screens":[]}""")]
    [InlineData("""{"screens":[{"screenId":"s"}]}""")]
    [InlineData("not json")]
    public void A_response_with_no_usable_token_yields_null(string body) =>
        Assert.Null(LoungeClient.ParseLoungeToken(body));

    [Fact]
    public void Now_playing_events_are_read_out_of_the_length_prefixed_stream()
    {
        var payload = """[[1,["nowPlaying",{"videoId":"dQw4w9WgXcQ","state":"1","currentTime":"12.5"}]]]""";
        var body = $"{payload.Length}\n{payload}";

        var state = LoungeSession.ParseReceiverState(Assert.Single(LoungeSession.ParseChunks(body)));

        Assert.Equal("dQw4w9WgXcQ", state!.VideoId);
        Assert.Equal(LoungePlayerState.Playing, state.State);
        Assert.Equal(12.5, state.CurrentTime);
    }

    [Fact]
    public void Length_prefixes_are_UTF8_BYTE_counts_not_character_counts()
    {
        // The receiver prefixes each chunk with a byte count. Slicing by char index
        // desynchronises the whole stream the moment an event carries non-ASCII —
        // a Cyrillic video title is enough — and every later chunk is misread.
        var payload = """[[1,["nowPlaying",{"videoId":"dQw4w9WgXcQ","state":"1","title":"Кыргызстан"}]]]""";
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(payload);

        Assert.True(byteCount > payload.Length, "the fixture must actually be multi-byte");

        var state = LoungeSession.ParseReceiverState(
            Assert.Single(LoungeSession.ParseChunks($"{byteCount}\n{payload}")));

        Assert.Equal("dQw4w9WgXcQ", state!.VideoId);
        Assert.Equal(LoungePlayerState.Playing, state.State);
    }

    [Fact]
    public void A_non_ascii_chunk_does_not_desynchronise_the_chunks_after_it()
    {
        // The real damage of a char-indexed scan: the FOLLOWING chunk is lost too.
        var first = """[[1,["nowPlaying",{"videoId":"aBcDeFgHiJk","state":"2","title":"Кыргызстан"}]]]""";
        var second = """[[2,["nowPlaying",{"videoId":"dQw4w9WgXcQ","state":"1"}]]]""";

        var body =
            $"{System.Text.Encoding.UTF8.GetByteCount(first)}\n{first}" +
            $"{System.Text.Encoding.UTF8.GetByteCount(second)}\n{second}";

        var chunks = LoungeSession.ParseChunks(body);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("dQw4w9WgXcQ", LoungeSession.ParseReceiverState(chunks[1])!.VideoId);
    }

    [Fact]
    public void A_truncated_or_malformed_chunk_is_skipped_rather_than_throwing()
    {
        // The stream is a long poll; a partial trailing chunk is normal, not an error.
        Assert.Empty(LoungeSession.ParseChunks("99\n[[1,[\"nowPlaying\""));
        Assert.Empty(LoungeSession.ParseChunks("not-a-length\n[]"));
        Assert.Empty(LoungeSession.ParseChunks(string.Empty));
    }

    [Fact]
    public void An_unrecognised_event_is_ignored_rather_than_misread()
    {
        var payload = """[[7,["loungeStatus",{"devices":"[]"}]]]""";
        var body = $"{payload.Length}\n{payload}";

        Assert.Null(LoungeSession.ParseReceiverState(Assert.Single(LoungeSession.ParseChunks(body))));
    }

    [Theory]
    [InlineData("1", LoungePlayerState.Playing)]
    [InlineData("2", LoungePlayerState.Paused)]
    [InlineData("3", LoungePlayerState.Buffering)]
    [InlineData("-1", LoungePlayerState.Unstarted)]
    [InlineData("", LoungePlayerState.Unknown)]
    [InlineData("99", LoungePlayerState.Unknown)]
    public void Player_states_map_from_the_wire_numbers(string raw, LoungePlayerState expected) =>
        Assert.Equal(expected, LoungeSession.ParseState(raw));
}

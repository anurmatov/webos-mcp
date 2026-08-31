using Microsoft.Extensions.Logging;
using WebosMcp.Application;
using WebosMcp.Tests.Fakes;
using Xunit;

namespace WebosMcp.Tests;

/// <summary>
/// Correlating the receiver's reports, and the rule that keeps the correlation
/// honest.
///
/// The receiver does not announce "video X is playing" in one event. It sends
/// <c>nowPlaying</c> with the video id — typically still buffering — and then
/// <c>onStateChange</c> with the playing state and NO video id. Matching one report
/// at a time can therefore never confirm anything, which is a false negative on a TV
/// that is visibly playing the right video.
///
/// The correction is a fold, and a fold is exactly where a false POSITIVE would come
/// from instead. So these tests are written in pairs: the id-less state confirms when
/// it follows the requested video, and refuses when it follows anything else.
/// </summary>
public sealed class ReceiverStateCorrelationTests
{
    private const string Video = "dQw4w9WgXcQ";
    private const string Other = "aBcDeFgHiJk";

    private static LoungeReceiverState Fold(params LoungeReceiverState[] reports)
    {
        var tracker = new ReceiverStateTracker();
        LoungeReceiverState composite = new();

        foreach (var report in reports)
        {
            composite = tracker.Apply(report);
        }

        return composite;
    }

    [Fact]
    public void An_id_less_state_applies_to_the_video_last_announced()
    {
        var composite = Fold(
            new LoungeReceiverState(Video, LoungePlayerState.Buffering),
            new LoungeReceiverState(VideoId: null, LoungePlayerState.Playing));

        Assert.Equal(Video, composite.VideoId);
        Assert.Equal(LoungePlayerState.Playing, composite.State);
    }

    [Fact]
    public void An_id_less_state_is_attributed_to_the_LAST_video_not_the_wanted_one()
    {
        var composite = Fold(
            new LoungeReceiverState(Video, LoungePlayerState.Playing),
            new LoungeReceiverState(Other, LoungePlayerState.Buffering),
            new LoungeReceiverState(VideoId: null, LoungePlayerState.Playing));

        Assert.Equal(Other, composite.VideoId);
    }

    [Fact]
    public void A_newly_announced_video_RESETS_the_state_rather_than_inheriting_it()
    {
        // Without the reset, the playing state from the previous video would be
        // attributed to one that has only just been announced and is still loading.
        var composite = Fold(
            new LoungeReceiverState(Other, LoungePlayerState.Playing),
            new LoungeReceiverState(Video, LoungePlayerState.Cued));

        Assert.Equal(Video, composite.VideoId);
        Assert.Equal(LoungePlayerState.Cued, composite.State);
    }

    [Fact]
    public void A_new_video_announced_WITHOUT_a_state_does_not_inherit_the_previous_one()
    {
        // The case the reset actually exists for, and the one the earlier tests
        // missed: nowPlaying frequently carries an id and no state field at all. If
        // the previous video's Playing survived that, the very next id-less event —
        // or nothing at all — would confirm a video that has not started. Found by
        // mutating the reset away and watching every test stay green.
        var composite = Fold(
            new LoungeReceiverState(Other, LoungePlayerState.Playing),
            new LoungeReceiverState(Video));

        Assert.Equal(Video, composite.VideoId);
        Assert.NotEqual(LoungePlayerState.Playing, composite.State);
    }

    [Fact]
    public void A_repeat_report_for_the_SAME_video_updates_rather_than_resets()
    {
        var composite = Fold(
            new LoungeReceiverState(Video, LoungePlayerState.Buffering),
            new LoungeReceiverState(Video, LoungePlayerState.Playing));

        Assert.Equal(Video, composite.VideoId);
        Assert.Equal(LoungePlayerState.Playing, composite.State);
    }

    [Fact]
    public void A_repeat_report_carrying_no_state_does_not_erase_the_known_one()
    {
        var composite = Fold(
            new LoungeReceiverState(Video, LoungePlayerState.Playing),
            new LoungeReceiverState(Video, LoungePlayerState.Unknown, CurrentTime: 30));

        Assert.Equal(LoungePlayerState.Playing, composite.State);
        Assert.Equal(30, composite.CurrentTime);
    }

    [Fact]
    public void A_state_arriving_before_any_video_was_announced_stays_unattributed()
    {
        var composite = Fold(new LoungeReceiverState(VideoId: null, LoungePlayerState.Playing));

        Assert.Null(composite.VideoId);
        Assert.Equal(LoungePlayerState.Playing, composite.State);
    }

    [Fact]
    public void Volume_and_autoplay_reports_do_not_disturb_the_playing_picture()
    {
        // These arrive interleaved with playback events and carry neither id nor
        // player state; folding them must be a no-op for what is playing.
        var composite = Fold(
            new LoungeReceiverState(Video, LoungePlayerState.Playing),
            new LoungeReceiverState(Volume: 40),
            new LoungeReceiverState(AutoplayEnabled: true));

        Assert.Equal(Video, composite.VideoId);
        Assert.Equal(LoungePlayerState.Playing, composite.State);
    }

    // ---- what gets logged about all this -----------------------------------

    [Fact]
    public async Task Receiver_events_are_logged_by_name_and_state_for_diagnosis()
    {
        // A failed verification otherwise leaves no evidence beyond "nothing
        // matched", which is what made the split-event fault so slow to find.
        var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddProvider(capture);
        });

        var harness = new TestHarness(loggerFactory: factory);
        harness.Lounge.Session!.Reports.AddRange([
            new LoungeReceiverState(Video, LoungePlayerState.Buffering, EventName: "nowPlaying"),
            new LoungeReceiverState(VideoId: null, LoungePlayerState.Playing, EventName: "onStateChange"),
        ]);

        await harness.Control.PlayYouTubeAsync(Video, CancellationToken.None);

        var lines = capture.Lines.ToArray();

        Assert.Contains(lines, line => line.Contains("nowPlaying", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("onStateChange", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Playing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Event_logging_never_prints_a_lounge_token_or_a_request_uri()
    {
        // The token is kept out of the command path precisely so request logging
        // cannot print it. A diagnostic that reintroduced it here would undo that,
        // and this is a public repository's idea of a bad afternoon.
        var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(capture);
        });

        var harness = new TestHarness(loggerFactory: factory);
        harness.Lounge.Session!.Reports.Add(new LoungeReceiverState(Video, LoungePlayerState.Playing));

        await harness.Control.PlayYouTubeAsync(Video, CancellationToken.None);

        foreach (var line in capture.Lines)
        {
            Assert.DoesNotContain("loungeIdToken", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("X-YouTube-LoungeId-Token", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/api/lounge/", line, StringComparison.OrdinalIgnoreCase);
        }
    }
}

using System.Text.Json;
using WebosMcp.Domain;
using WebosMcp.Tests.Fakes;
using Xunit;

namespace WebosMcp.Tests;

/// <summary>
/// Deep-link versus bounded fallback. The label the tool returns must match the
/// path that actually ran — a caller is never left guessing.
/// </summary>
public sealed class ContentPathTests
{
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

    [Fact]
    public async Task Youtube_search_uses_the_deep_link_when_the_tv_accepts_it()
    {
        var harness = new TestHarness();

        var result = await harness.Control.SearchYouTubeAsync("cooking pasta", CancellationToken.None);

        Assert.Equal(ActionPath.DeepLink, result.Path);

        var call = harness.Connection.Calls.Single(c => c.Target == "ssap://system.launcher/launch");
        using var payload = JsonDocument.Parse(call.Payload!);
        var target = payload.RootElement.GetProperty("contentTarget").GetString();
        Assert.Contains("cooking%20pasta", target);

        // No fallback sequence ran.
        Assert.DoesNotContain(harness.Connection.Calls, c => c.Kind == "button");
    }

    [Fact]
    public async Task Youtube_search_falls_back_to_a_bounded_sequence_and_says_so()
    {
        var connection = new FakeSsapConnection();

        // The deep-linked launch is rejected once; the plain launch then succeeds.
        connection.TransientFailures["ssap://system.launcher/launch"] =
            new Queue<Exception>([new TvException(TvErrorCode.TvError, "invalid contentTarget")]);

        var harness = new TestHarness(connection);
        var result = await harness.Control.SearchYouTubeAsync("weather", CancellationToken.None);

        Assert.Equal(ActionPath.Fallback, result.Path);
        Assert.Contains("bounded", result.Detail, StringComparison.OrdinalIgnoreCase);

        // The fallback is a fixed, bounded sequence: launch, focus, type, submit.
        Assert.Contains(harness.Connection.Calls, c => c.Kind == "button" && c.Target == "HOME");
        Assert.Contains("ssap://com.webos.service.ime/insertText", harness.Connection.RequestUris);
        Assert.Contains("ssap://com.webos.service.ime/sendEnterKey", harness.Connection.RequestUris);

        var typed = harness.Connection.Calls
            .First(c => c.Target == "ssap://com.webos.service.ime/insertText");
        using var payload = JsonDocument.Parse(typed.Payload!);
        Assert.Equal("weather", payload.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task The_fallback_sequence_is_bounded_not_a_retry_loop()
    {
        var connection = new FakeSsapConnection();
        connection.TransientFailures["ssap://system.launcher/launch"] =
            new Queue<Exception>([new TvException(TvErrorCode.TvError, "invalid contentTarget")]);

        var harness = new TestHarness(connection);
        await harness.Control.SearchYouTubeAsync("weather", CancellationToken.None);

        // Two launches total: the rejected deep link, then the fallback's plain launch.
        Assert.Equal(2, harness.Connection.Calls.Count(c => c.Target == "ssap://system.launcher/launch"));
        Assert.Single(harness.Connection.Calls, c => c.Kind == "button");
    }

    [Fact]
    public async Task Youtube_play_deep_links_the_bare_video_id()
    {
        var harness = new TestHarness();

        var result = await harness.Control.PlayYouTubeAsync(
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ", CancellationToken.None);

        Assert.Equal(ActionPath.DeepLink, result.Path);

        var call = harness.Connection.Calls.Single(c => c.Target == "ssap://system.launcher/launch");
        using var payload = JsonDocument.Parse(call.Payload!);
        Assert.Equal(
            "https://www.youtube.com/tv?v=dQw4w9WgXcQ",
            payload.RootElement.GetProperty("contentTarget").GetString());
    }

    [Fact]
    public async Task A_pairing_failure_during_search_is_not_swallowed_by_the_fallback()
    {
        var connection = new FakeSsapConnection { RegisterFailure = TvException.PairingRequired() };
        var harness = new TestHarness(connection);

        var ex = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.SearchYouTubeAsync("weather", CancellationToken.None));

        // The fallback exists for deep-link rejection, not for masking the error contract.
        Assert.Equal(TvErrorCode.PairingRequired, ex.Code);
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebosMcp.Application;
using WebosMcp.Domain;
using WebosMcp.Infrastructure;
using WebosMcp.Tests.Fakes;
using Xunit;

namespace WebosMcp.Tests;

/// <summary>
/// DIAL endpoint resolution. Physical acceptance found tv_youtube_play reporting
/// TV_UNSUPPORTED_CAPABILITY ("no DIAL endpoint") against a TV that was in fact
/// advertising DIAL on port 2038: the containerized server cannot receive SSDP
/// multicast, and the direct probes covered 1754/3000/8080/9080 but not 2038.
///
/// These tests pin resolution working with NO multicast available at all, which
/// is the deployment shape that failed.
/// </summary>
public sealed class DialResolutionTests
{
    private const string Host = "192.0.2.10";
    private const string MulticastTarget = "239.255.255.250:1900";
    private const string UnicastTarget = $"{Host}:1900";

    private static (DialClient Client, StubDialHttpHandler Http, FakeSsdpChannel Ssdp) Build(
        Dictionary<string, string>? endpoints = null,
        Dictionary<string, IReadOnlyList<string>>? ssdp = null,
        Action<WebosMcpOptions>? configure = null)
    {
        var options = new WebosMcpOptions { Host = Host };
        configure?.Invoke(options);

        var http = new StubDialHttpHandler(endpoints ?? []);
        var channel = new FakeSsdpChannel(ssdp);

        var client = new DialClient(
            new HttpClient(http),
            Options.Create(options),
            channel,
            NullLogger<DialClient>.Instance);

        return (client, http, channel);
    }

    // ---- the regression this round exists for -------------------------------

    [Fact]
    public async Task Port_2038_is_probed_directly_so_LG_resolves_without_multicast()
    {
        var (client, _, ssdp) = Build(new Dictionary<string, string>
        {
            [$"http://{Host}:2038/"] = $"http://{Host}:2038/apps/",
        });

        var resolved = await client.ResolveApplicationUrlAsync(CancellationToken.None);

        Assert.Equal($"http://{Host}:2038/apps/", resolved?.ApplicationUrl.AbsoluteUri);

        // The point of the fix: no SSDP of any kind was needed.
        Assert.Empty(ssdp.Searched);
    }

    [Fact]
    public async Task Every_default_port_including_2038_is_actually_probed()
    {
        // Nothing answers, so resolution must have tried them all.
        var (client, http, _) = Build();

        await client.ResolveApplicationUrlAsync(CancellationToken.None);

        foreach (var port in new[] { 2038, 1754, 3000, 8080, 9080 })
        {
            Assert.Contains($"http://{Host}:{port}/", http.Requested);
        }
    }

    [Fact]
    public async Task When_two_ports_answer_the_configured_order_wins_not_the_race()
    {
        // Probes run concurrently; the result must still be deterministic.
        var (client, _, _) = Build(new Dictionary<string, string>
        {
            [$"http://{Host}:8080/"] = $"http://{Host}:8080/apps/",
            [$"http://{Host}:2038/"] = $"http://{Host}:2038/apps/",
        });

        var resolved = await client.ResolveApplicationUrlAsync(CancellationToken.None);

        Assert.Equal($"http://{Host}:2038/apps/", resolved?.ApplicationUrl.AbsoluteUri);
    }

    // ---- the configurable escape hatch --------------------------------------

    [Fact]
    public async Task An_explicit_application_url_short_circuits_all_discovery()
    {
        var (client, http, ssdp) = Build(
            configure: o => o.DialApplicationUrl = $"http://{Host}:9999/apps/");

        var resolved = await client.ResolveApplicationUrlAsync(CancellationToken.None);

        Assert.Equal($"http://{Host}:9999/apps/", resolved?.ApplicationUrl.AbsoluteUri);
        Assert.Empty(http.Requested);
        Assert.Empty(ssdp.Searched);
    }

    [Fact]
    public async Task A_custom_port_list_is_honoured()
    {
        var (client, http, _) = Build(
            new Dictionary<string, string> { [$"http://{Host}:7000/"] = $"http://{Host}:7000/apps/" },
            configure: o => o.DialPorts = "7000");

        var resolved = await client.ResolveApplicationUrlAsync(CancellationToken.None);

        Assert.Equal($"http://{Host}:7000/apps/", resolved?.ApplicationUrl.AbsoluteUri);
        Assert.Equal([$"http://{Host}:7000/"], http.Requested);
    }

    [Fact]
    public async Task A_malformed_application_url_is_an_operator_error_not_an_unsupported_TV()
    {
        // Blaming the TV for a typo would send someone debugging the wrong thing.
        var (client, _, _) = Build(configure: o => o.DialApplicationUrl = "not-a-url");

        var error = await Assert.ThrowsAsync<TvException>(
            () => client.ResolveApplicationUrlAsync(CancellationToken.None));

        Assert.Equal(TvErrorCode.InvalidInput, error.Code);
    }

    [Fact]
    public void A_malformed_port_list_is_rejected_rather_than_silently_dropped()
    {
        var options = new WebosMcpOptions { DialPorts = "2038,not-a-port" };

        Assert.Throws<TvException>(() => options.ResolvedDialPorts);
    }

    // ---- unicast SSDP: discovery without multicast ---------------------------

    [Fact]
    public async Task Unicast_ssdp_resolves_an_endpoint_the_direct_probes_do_not_know_about()
    {
        // The TV advertises a port/path outside the probe list. Multicast stays
        // silent, as it would inside a bridge-mode container.
        var (client, _, ssdp) = Build(
            new Dictionary<string, string> { [$"http://{Host}:5000/dd.xml"] = $"http://{Host}:5000/apps/" },
            new Dictionary<string, IReadOnlyList<string>>
            {
                [UnicastTarget] = [FakeSsdpChannel.Notify($"http://{Host}:5000/dd.xml")],
            });

        var resolved = await client.ResolveApplicationUrlAsync(CancellationToken.None);

        Assert.Equal($"http://{Host}:5000/apps/", resolved?.ApplicationUrl.AbsoluteUri);
        Assert.Equal([UnicastTarget], ssdp.Searched);
        Assert.DoesNotContain(MulticastTarget, ssdp.Searched);
    }

    [Fact]
    public async Task Multicast_is_tried_only_after_unicast_has_failed()
    {
        var (client, _, ssdp) = Build(
            new Dictionary<string, string> { [$"http://{Host}:5000/dd.xml"] = $"http://{Host}:5000/apps/" },
            new Dictionary<string, IReadOnlyList<string>>
            {
                [MulticastTarget] = [FakeSsdpChannel.Notify($"http://{Host}:5000/dd.xml")],
            });

        var resolved = await client.ResolveApplicationUrlAsync(CancellationToken.None);

        Assert.Equal($"http://{Host}:5000/apps/", resolved?.ApplicationUrl.AbsoluteUri);
        Assert.Equal([UnicastTarget, MulticastTarget], ssdp.Searched);
    }

    [Fact]
    public async Task An_ssdp_response_with_no_usable_location_is_skipped()
    {
        var (client, _, _) = Build(ssdp: new Dictionary<string, IReadOnlyList<string>>
        {
            [UnicastTarget] = ["HTTP/1.1 200 OK\r\nST: urn:dial-multiscreen-org:service:dial:1\r\n\r\n"],
        });

        Assert.Null(await client.ResolveApplicationUrlAsync(CancellationToken.None));
    }

    // ---- honest unsupported, preserved ---------------------------------------

    [Fact]
    public async Task A_TV_with_no_DIAL_endpoint_still_resolves_to_null()
    {
        // Null is what TvControlService turns into TV_UNSUPPORTED_CAPABILITY. Widening
        // the search must not turn "absent" into a false positive.
        var (client, _, ssdp) = Build();

        Assert.Null(await client.ResolveApplicationUrlAsync(CancellationToken.None));
        Assert.Equal([UnicastTarget, MulticastTarget], ssdp.Searched);
    }

    [Fact]
    public async Task A_port_that_answers_without_the_DIAL_header_is_not_a_DIAL_endpoint()
    {
        // Plenty of things answer 200 on 3000/8080. Only Application-URL counts.
        var (client, _, _) = Build(new Dictionary<string, string>());

        Assert.Null(await client.ResolveApplicationUrlAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Resolution_is_cached_so_a_second_call_does_not_reprobe()
    {
        var (client, http, _) = Build(new Dictionary<string, string>
        {
            [$"http://{Host}:2038/"] = $"http://{Host}:2038/apps/",
        });

        var first = await client.ResolveApplicationUrlAsync(CancellationToken.None);
        var probesAfterFirst = http.Requested.Count;
        var second = await client.ResolveApplicationUrlAsync(CancellationToken.None);

        Assert.Equal(first?.ApplicationUrl, second?.ApplicationUrl);
        Assert.False(first?.FromCache);
        Assert.True(second?.FromCache);
        Assert.Equal(probesAfterFirst, http.Requested.Count);
    }

    // ---- self-heal: a TV reboot moved the DIAL port --------------------------

    private const int OldPort = 1700;
    private const int NewPort = 1100;
    private const string Video = "dQw4w9WgXcQ";

    /// <summary>
    /// A real <see cref="DialClient"/> over a TV whose DIAL port can move, with the
    /// old port already resolved and cached, as it is after the first call of a
    /// long-running server.
    /// </summary>
    private static async Task<(TestHarness Harness, MovingDialHttpHandler Tv, CountingDialClient Dial)> CachedOnOldPortAsync(
        TimeSpan? httpTimeout = null,
        Action<WebosMcpOptions>? configure = null)
    {
        var tv = new MovingDialHttpHandler(Host, OldPort);
        var options = new WebosMcpOptions { Host = Host, DialPorts = $"{OldPort},{NewPort}" };
        configure?.Invoke(options);

        var client = new DialClient(
            new HttpClient(tv) { Timeout = httpTimeout ?? TimeSpan.FromSeconds(30) },
            Options.Create(options),
            new FakeSsdpChannel(),
            NullLogger<DialClient>.Instance);

        var dial = new CountingDialClient(client);
        var warm = await dial.ResolveApplicationUrlAsync(CancellationToken.None);
        Assert.Equal(OldPort, warm?.ApplicationUrl.Port);

        var harness = new TestHarness(dial: dial);
        harness.Lounge.Session!.Reports.Add(new LoungeReceiverState(Video, LoungePlayerState.Playing));
        return (harness, tv, dial);
    }

    [Fact]
    public async Task A_refused_cached_port_is_re_resolved_once_and_play_continues()
    {
        var (harness, tv, dial) = await CachedOnOldPortAsync();
        tv.CurrentPort = NewPort;
        var before = dial.ResolveCount;

        var result = await harness.Control.PlayYouTubeAsync(Video, CancellationToken.None);

        Assert.True(result.ExactVideoConfirmed);
        Assert.Equal(2, dial.ResolveCount - before);
        Assert.Equal(NewPort, (await dial.ResolveApplicationUrlAsync(CancellationToken.None))?.ApplicationUrl.Port);
    }

    [Fact]
    public async Task A_cached_port_that_times_out_with_a_live_caller_is_re_resolved_once()
    {
        var (harness, tv, dial) = await CachedOnOldPortAsync(httpTimeout: TimeSpan.FromMilliseconds(200));
        tv.StatusByPort[OldPort] = DialPortBehaviour.Hang;
        tv.CurrentPort = NewPort;
        var before = dial.ResolveCount;

        var result = await harness.Control.PlayYouTubeAsync(Video, CancellationToken.None);

        Assert.True(result.ExactVideoConfirmed);
        Assert.Equal(2, dial.ResolveCount - before);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_mistaken_for_a_moved_port()
    {
        var (harness, tv, dial) = await CachedOnOldPortAsync();
        tv.StatusByPort[OldPort] = DialPortBehaviour.Hang;
        tv.CurrentPort = NewPort;
        var before = dial.ResolveCount;
        using var cts = new CancellationTokenSource();

        var play = harness.Control.PlayYouTubeAsync(Video, cts.Token);
        await tv.HangStarted.Task;
        await cts.CancelAsync();

        var error = await Assert.ThrowsAsync<TvException>(() => play);
        Assert.Equal(TvErrorCode.TvUnreachable, error.Code);
        Assert.Equal(1, dial.ResolveCount - before);
    }

    [Fact]
    public async Task A_404_on_the_cached_port_is_not_installed_and_never_re_resolves()
    {
        var (harness, tv, dial) = await CachedOnOldPortAsync();
        tv.StatusByPort[OldPort] = DialPortBehaviour.NotFound;
        var before = dial.ResolveCount;

        var error = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.PlayYouTubeAsync(Video, CancellationToken.None));

        Assert.Equal(TvErrorCode.TvUnsupportedCapability, error.Code);
        Assert.Contains("not installed", error.Message);
        Assert.Equal(1, dial.ResolveCount - before);
    }

    [Fact]
    public async Task A_403_on_the_cached_port_is_a_TV_error_and_never_re_resolves()
    {
        var (harness, tv, dial) = await CachedOnOldPortAsync();
        tv.StatusByPort[OldPort] = DialPortBehaviour.Forbidden;
        var before = dial.ResolveCount;

        var error = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.PlayYouTubeAsync(Video, CancellationToken.None));

        Assert.Equal(TvErrorCode.TvError, error.Code);
        Assert.Equal(1, dial.ResolveCount - before);
    }

    [Fact]
    public async Task A_configured_url_that_is_unreachable_is_never_re_resolved()
    {
        var (harness, tv, dial) = await CachedOnOldPortAsync(
            configure: o => o.DialApplicationUrl = $"http://{Host}:{OldPort}/apps/");
        tv.CurrentPort = NewPort;
        var before = dial.ResolveCount;

        var error = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.PlayYouTubeAsync(Video, CancellationToken.None));

        Assert.Equal(TvErrorCode.TvUnreachable, error.Code);
        Assert.Equal(1, dial.ResolveCount - before);
    }

    [Fact]
    public async Task A_fresh_resolve_to_the_same_url_rethrows_the_original_error()
    {
        // Discovery still finds the old port, but its status endpoint refuses.
        var (harness, tv, dial) = await CachedOnOldPortAsync();
        tv.StatusByPort[OldPort] = DialPortBehaviour.Refused;
        var before = dial.ResolveCount;

        var error = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.PlayYouTubeAsync(Video, CancellationToken.None));

        Assert.Equal(TvErrorCode.TvUnreachable, error.Code);
        Assert.Contains("Could not read DIAL status", error.Message);
        Assert.Equal(2, dial.ResolveCount - before);
    }

    [Fact]
    public async Task A_fresh_resolve_that_finds_nothing_rethrows_the_original_error()
    {
        var (harness, tv, dial) = await CachedOnOldPortAsync();
        tv.CurrentPort = 9999; // outside the probe list, and SSDP stays silent
        var before = dial.ResolveCount;

        var error = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.PlayYouTubeAsync(Video, CancellationToken.None));

        Assert.Equal(TvErrorCode.TvUnreachable, error.Code);
        Assert.Equal(2, dial.ResolveCount - before);
    }

    [Fact]
    public async Task The_receiver_controls_heal_a_moved_port_too()
    {
        var (harness, tv, dial) = await CachedOnOldPortAsync();
        tv.CurrentPort = NewPort;
        var before = dial.ResolveCount;

        await harness.Control.YouTubeNowPlayingAsync(CancellationToken.None);

        Assert.Equal(2, dial.ResolveCount - before);
        Assert.Contains("screen-1", harness.Lounge.ConnectedScreenIds);
    }

    [Fact]
    public async Task A_late_invalidate_does_not_drop_a_newer_cached_url()
    {
        var tv = new MovingDialHttpHandler(Host, OldPort);
        var client = new DialClient(
            new HttpClient(tv),
            Options.Create(new WebosMcpOptions { Host = Host, DialPorts = $"{OldPort},{NewPort}" }),
            new FakeSsdpChannel(),
            NullLogger<DialClient>.Instance);

        var stale = (await client.ResolveApplicationUrlAsync(CancellationToken.None))!.ApplicationUrl;
        tv.CurrentPort = NewPort;

        // One caller heals the cache onto the new port...
        client.InvalidateApplicationUrl(stale);
        var fresh = await client.ResolveApplicationUrlAsync(CancellationToken.None);
        Assert.Equal(NewPort, fresh?.ApplicationUrl.Port);

        // ...then a slower caller invalidates the same stale URL.
        client.InvalidateApplicationUrl(stale);

        var cached = await client.ResolveApplicationUrlAsync(CancellationToken.None);
        Assert.True(cached?.FromCache);
        Assert.Equal(NewPort, cached?.ApplicationUrl.Port);
    }

    [Fact]
    public async Task A_resolution_reports_its_strategy_and_whether_it_was_cached()
    {
        var (client, _, _) = Build(new Dictionary<string, string>
        {
            [$"http://{Host}:2038/"] = $"http://{Host}:2038/apps/",
        });

        var first = await client.ResolveApplicationUrlAsync(CancellationToken.None);
        var second = await client.ResolveApplicationUrlAsync(CancellationToken.None);

        Assert.Equal(new DialResolution(new Uri($"http://{Host}:2038/apps/"), DialResolutionStrategy.DirectProbe, false), first);
        Assert.Equal(first! with { FromCache = true }, second);
        Assert.Equal("direct-probe", DialClient.StrategyName(DialResolutionStrategy.DirectProbe));
        Assert.Equal("multicast-ssdp", DialClient.StrategyName(DialResolutionStrategy.MulticastSsdp));
    }
}

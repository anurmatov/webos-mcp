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

        Assert.Equal($"http://{Host}:2038/apps/", resolved?.AbsoluteUri);

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

        Assert.Equal($"http://{Host}:2038/apps/", resolved?.AbsoluteUri);
    }

    // ---- the configurable escape hatch --------------------------------------

    [Fact]
    public async Task An_explicit_application_url_short_circuits_all_discovery()
    {
        var (client, http, ssdp) = Build(
            configure: o => o.DialApplicationUrl = $"http://{Host}:9999/apps/");

        var resolved = await client.ResolveApplicationUrlAsync(CancellationToken.None);

        Assert.Equal($"http://{Host}:9999/apps/", resolved?.AbsoluteUri);
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

        Assert.Equal($"http://{Host}:7000/apps/", resolved?.AbsoluteUri);
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

        Assert.Equal($"http://{Host}:5000/apps/", resolved?.AbsoluteUri);
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

        Assert.Equal($"http://{Host}:5000/apps/", resolved?.AbsoluteUri);
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

        Assert.Equal(first, second);
        Assert.Equal(probesAfterFirst, http.Requested.Count);
    }
}

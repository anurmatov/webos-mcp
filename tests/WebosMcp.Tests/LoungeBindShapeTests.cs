using System.Net;
using System.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebosMcp.Application;
using WebosMcp.Domain;
using WebosMcp.Infrastructure;
using WebosMcp.Tests.Fakes;
using Xunit;

namespace WebosMcp.Tests;

/// <summary>
/// The Lounge bind handshake's wire shape.
///
/// Physical acceptance found every YouTube tool failing at bind while a reference
/// client controlling the SAME receiver connected immediately. The difference was
/// entirely in the handshake: the receiver and device metadata belong in the POST
/// FORM BODY — with the receiver's own screen id as `id` — and only the channel
/// parameters belong in the query. The old shape put the metadata in the query
/// with a randomly generated client id and posted a bare `count=0`.
///
/// Nothing but a request-capturing test can pin this: the shape is what the
/// receiver validates, and every unit test above this layer passes either way.
/// </summary>
public sealed class LoungeBindShapeTests
{
    private const string Token = "lounge-token-xyz";
    private const string ScreenId = "screen-abc123";

    /// <summary>A bind response carrying the session ids, in the real chunked framing.</summary>
    private static string BindResponse()
    {
        var payload = """[[0,["c","SID-1","",8]],[1,["S","GSESSION-1"]]]""";
        return $"{System.Text.Encoding.UTF8.GetByteCount(payload)}\n{payload}";
    }

    private static (LoungeClient Client, CapturingLoungeHandler Http) Build(string? bindBody = null)
    {
        var http = new CapturingLoungeHandler(
            (HttpStatusCode.OK, $$"""{"screens":[{"screenId":"{{ScreenId}}","loungeToken":"{{Token}}"}]}"""),
            (HttpStatusCode.OK, bindBody ?? BindResponse()));

        var factory = NullLoggerFactory.Instance;

        return (new LoungeClient(
            new HttpClient(http),
            Options.Create(new WebosMcpOptions { LoungeDeviceName = "webos-mcp-test" }),
            factory,
            NullLogger<LoungeClient>.Instance), http);
    }

    private static Dictionary<string, string> Fields(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                parts => HttpUtility.UrlDecode(parts[0]),
                parts => parts.Length > 1 ? HttpUtility.UrlDecode(parts[1]) : string.Empty);

    private static Dictionary<string, string> Query(string url)
    {
        var parsed = HttpUtility.ParseQueryString(new Uri(url).Query);
        return parsed.AllKeys.Where(k => k is not null)
            .ToDictionary(k => k!, k => parsed[k] ?? string.Empty);
    }

    [Fact]
    public async Task The_bind_posts_the_receiver_and_device_metadata_as_FORM_FIELDS()
    {
        var (client, http) = Build();

        await client.ConnectAsync(ScreenId, CancellationToken.None);

        var bind = http.Requests[1];
        var fields = Fields(bind.Body);

        Assert.Equal("REMOTE_CONTROL", fields["device"]);
        Assert.Equal("webos-mcp-test", fields["name"]);
        Assert.Equal("3", fields["mdx-version"]);
        Assert.Equal(Token, fields["loungeIdToken"]);
    }

    [Fact]
    public async Task The_id_field_is_the_RECEIVERS_screen_id_not_a_generated_client_id()
    {
        // The old shape sent a random GUID here, which is why the receiver refused
        // the session: nothing tied the remote to the running receiver.
        var (client, http) = Build();

        await client.ConnectAsync(ScreenId, CancellationToken.None);

        Assert.Equal(ScreenId, Fields(http.Requests[1].Body)["id"]);
    }

    [Fact]
    public async Task The_bind_query_carries_ONLY_the_channel_parameters()
    {
        var (client, http) = Build();

        await client.ConnectAsync(ScreenId, CancellationToken.None);

        var query = Query(http.Requests[1].Url);

        Assert.Equal(["CVER", "RID", "VER", "auth_failure_option"], query.Keys.Order().ToArray());
        Assert.Equal("8", query["VER"]);
        Assert.Equal("1", query["CVER"]);
        Assert.Equal("send_error", query["auth_failure_option"]);
    }

    [Fact]
    public async Task The_token_never_appears_in_the_bind_URL()
    {
        // Belt and braces with the log filtering: a token that is not in a URL
        // cannot be printed by request logging in the first place.
        var (client, http) = Build();

        await client.ConnectAsync(ScreenId, CancellationToken.None);

        Assert.DoesNotContain(Token, http.Requests[1].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_bind_is_not_a_bare_count_post()
    {
        // The old body was exactly "count=0" and the receiver refused it.
        var (client, http) = Build();

        await client.ConnectAsync(ScreenId, CancellationToken.None);

        Assert.NotEqual("count=0", http.Requests[1].Body);
    }

    [Fact]
    public async Task The_token_is_also_presented_as_a_header()
    {
        var (client, http) = Build();

        await client.ConnectAsync(ScreenId, CancellationToken.None);

        Assert.Equal(Token, http.Requests[1].TokenHeader);
    }

    [Fact]
    public async Task A_bind_response_with_session_ids_yields_a_usable_session()
    {
        var (client, _) = Build();

        Assert.NotNull(await client.ConnectAsync(ScreenId, CancellationToken.None));
    }

    [Fact]
    public async Task A_bind_response_with_no_session_ids_yields_no_session()
    {
        // Reported by callers as TV_UNSUPPORTED_CAPABILITY — never a false success.
        var (client, _) = Build(bindBody: "4\n[[]]");

        Assert.Null(await client.ConnectAsync(ScreenId, CancellationToken.None));
    }

    [Fact]
    public async Task The_bind_body_carries_the_proven_app_identity_and_capabilities()
    {
        // app is not cosmetic — it is part of what the receiver validates.
        var (client, http) = Build();

        await client.ConnectAsync(ScreenId, CancellationToken.None);
        var fields = Fields(http.Requests[1].Body);

        Assert.Equal("youtube-desktop", fields["app"]);
        Assert.Equal("que,dsdtr,atp", fields["capabilities"]);
        Assert.True(fields.ContainsKey("deviceContext"));
        Assert.False(fields.ContainsKey("method"));
    }

    // ---- command and event-stream shapes -----------------------------------

    private static async Task<CapturingLoungeHandler> ConnectedAsync()
    {
        var (client, http) = Build();
        var session = await client.ConnectAsync(ScreenId, CancellationToken.None);
        Assert.NotNull(session);

        await session!.SendAsync("pause", new Dictionary<string, string> { ["x"] = "1" }, CancellationToken.None);
        return http;
    }

    [Fact]
    public async Task The_command_query_carries_the_app_and_session_fields_and_nothing_else()
    {
        var query = Query((await ConnectedAsync()).Requests[2].Url);

        Assert.Equal("youtube-desktop", query["app"]);
        Assert.Equal("SID-1", query["SID"]);
        Assert.Equal("GSESSION-1", query["gsessionid"]);
        Assert.Equal("8", query["VER"]);

        // The bind-body metadata does not belong on this path.
        foreach (var stray in new[] { "id", "mdx-version", "ui", "t", "name", "device" })
        {
            Assert.False(query.ContainsKey(stray), $"'{stray}' should not be in the command query");
        }
    }

    [Fact]
    public async Task The_command_carries_the_token_as_a_HEADER_and_never_in_the_url()
    {
        // Correcting the record from the previous round: bind was fixed then, but
        // commands still carried the token in the query under log-filter protection.
        var http = await ConnectedAsync();

        Assert.Equal(Token, http.Requests[2].TokenHeader);
        Assert.DoesNotContain(Token, http.Requests[2].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_command_body_is_the_indexed_req0_form()
    {
        var fields = Fields((await ConnectedAsync()).Requests[2].Body);

        Assert.Equal("1", fields["count"]);
        Assert.Equal("pause", fields["req0__sc"]);
        Assert.Equal("1", fields["req0_x"]);
    }

    [Fact]
    public async Task The_event_subscription_query_matches_the_hardware_proven_shape()
    {
        // The subscription re-presents the remote's identity; the receiver will not
        // feed a poll that omits it. This is deliberately FULLER than the command
        // query — the two are different requests and only the command one is proven
        // in the shorter form.
        var (client, http) = Build();
        var session = await client.ConnectAsync(ScreenId, CancellationToken.None);

        // Subscribing is what issues the poll — it returns only once the receiver has
        // accepted it, so the request is on the wire by the time this line completes.
        await using var subscription = await session!.SubscribeAsync(CancellationToken.None);

        var query = Query(http.Requests[2].Url);

        Assert.Equal("webos-mcp-test", query["name"]);
        Assert.Equal(Token, query["loungeIdToken"]);
        Assert.Equal("REMOTE_CONTROL", query["device"]);
        Assert.Equal("youtube-desktop", query["app"]);
        Assert.Equal("8", query["VER"]);
        Assert.Equal("2", query["v"]);
        Assert.Equal("rpc", query["RID"]);
        Assert.Equal("SID-1", query["SID"]);
        Assert.Equal("GSESSION-1", query["gsessionid"]);
        Assert.Equal("0", query["CI"]);
        Assert.Equal("xmlhttp", query["TYPE"]);
        Assert.Equal("0", query["AID"]);
    }

    [Fact]
    public async Task Subscribing_issues_the_poll_BEFORE_it_returns()
    {
        // The readiness barrier, at the wire. Subscribing must not hand back a lazy
        // stream that opens on first read: the caller sends its command the moment
        // this returns, and an unopened stream would put the poll after the command
        // again. A lazy implementation leaves only two requests here.
        var (client, http) = Build();
        var session = await client.ConnectAsync(ScreenId, CancellationToken.None);

        await using var subscription = await session!.SubscribeAsync(CancellationToken.None);

        Assert.Equal(3, http.Requests.Count);
        Assert.Contains("TYPE=xmlhttp", http.Requests[2].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_poll_is_on_the_wire_before_the_command_that_follows_it()
    {
        // The ordering the physical fault turned on, asserted against request order
        // rather than call order — the two can diverge if the poll is opened lazily.
        var (client, http) = Build();
        var session = await client.ConnectAsync(ScreenId, CancellationToken.None);

        await using var subscription = await session!.SubscribeAsync(CancellationToken.None);
        await session.SendAsync("setPlaylist", new Dictionary<string, string>(), CancellationToken.None);

        Assert.Contains("TYPE=xmlhttp", http.Requests[2].Url, StringComparison.Ordinal);
        Assert.Contains("req0__sc=setPlaylist", http.Requests[3].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_poll_reports_failure_rather_than_a_stream_that_observes_nothing()
    {
        // A subscription that cannot be established has to fail loudly: the caller
        // sends its command next, and a silently dead stream would turn that into an
        // unverifiable command reported as never observed.
        var http = new CapturingLoungeHandler(
            pollStatus: HttpStatusCode.Forbidden,
            (HttpStatusCode.OK, $$"""{"screens":[{"screenId":"{{ScreenId}}","loungeToken":"{{Token}}"}]}"""),
            (HttpStatusCode.OK, BindResponse()));

        var client = new LoungeClient(
            new HttpClient(http),
            Options.Create(new WebosMcpOptions { LoungeDeviceName = "webos-mcp-test" }),
            NullLoggerFactory.Instance,
            NullLogger<LoungeClient>.Instance);

        var session = await client.ConnectAsync(ScreenId, CancellationToken.None);

        var error = await Assert.ThrowsAsync<TvException>(
            () => session!.SubscribeAsync(CancellationToken.None));

        Assert.Contains("event stream", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_command_query_is_left_exactly_as_hardware_proved_it()
    {
        // Command delivery is physically proven: the requested video really started.
        // Fixing the subscription must not disturb it, so this pins the command
        // query against being "unified" with the fuller subscription shape.
        var http = await ConnectedAsync();
        var query = Query(http.Requests[2].Url);

        Assert.Equal(
            ["CVER", "RID", "SID", "VER", "app", "auth_failure_option", "gsessionid"],
            query.Keys.Order().ToArray());
        Assert.DoesNotContain(Token, http.Requests[2].Url, StringComparison.Ordinal);
        Assert.Equal(Token, http.Requests[2].TokenHeader);
    }

    [Fact]
    public async Task The_screen_id_is_what_the_token_is_requested_for()
    {
        var (client, http) = Build();

        await client.ConnectAsync(ScreenId, CancellationToken.None);

        Assert.Equal(ScreenId, Fields(http.Requests[0].Body)["screen_ids"]);
    }
}

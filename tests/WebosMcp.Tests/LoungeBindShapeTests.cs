using System.Net;
using System.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebosMcp.Application;
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
    public async Task The_screen_id_is_what_the_token_is_requested_for()
    {
        var (client, http) = Build();

        await client.ConnectAsync(ScreenId, CancellationToken.None);

        Assert.Equal(ScreenId, Fields(http.Requests[0].Body)["screen_ids"]);
    }
}

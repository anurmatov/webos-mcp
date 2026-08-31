using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebosMcp.Application;
using WebosMcp.Domain;
using WebosMcp.Infrastructure;
using WebosMcp.Tests.Fakes;
using Xunit;

namespace WebosMcp.Tests;

/// <summary>
/// DIAL application requests: the sender origin, and what a non-404 refusal means.
///
/// A DIAL application endpoint is origin-checked. Without the origin the app
/// expects it answers 403 — and 403 was being mapped to null, the same value that
/// means "not installed". So a TV with YouTube installed reported
/// TV_UNSUPPORTED_CAPABILITY ("not installed on this TV"), which is a false
/// statement about the TV and points debugging at the wrong thing entirely.
///
/// 404 is the ONLY status that means not-installed.
/// </summary>
public sealed class DialApplicationRequestTests
{
    private const string YouTubeOrigin = "https://www.youtube.com";
    private static readonly Uri AppsUrl = new("http://192.0.2.10:2038/apps/");

    private const string RunningBody =
        """<service xmlns="urn:dial-multiscreen-org:schemas:dial"><name>YouTube</name><state>stopped</state></service>""";

    private static (DialClient Client, ScriptedDialHttpHandler Http) Build(
        HttpStatusCode status,
        string body = "")
    {
        var http = new ScriptedDialHttpHandler(status, body);

        var client = new DialClient(
            new HttpClient(http),
            Options.Create(new WebosMcpOptions { Host = "192.0.2.10" }),
            new FakeSsdpChannel(),
            NullLogger<DialClient>.Instance);

        return (client, http);
    }

    // ---- the authorised sender origin ---------------------------------------

    [Fact]
    public async Task The_app_status_GET_sends_the_YouTube_origin()
    {
        var (client, http) = Build(HttpStatusCode.OK, RunningBody);

        await client.GetAppStatusAsync(AppsUrl, "YouTube", CancellationToken.None);

        var sent = Assert.Single(http.Requests);
        Assert.Equal("GET", sent.Method);
        Assert.Equal(YouTubeOrigin, sent.Origin);
    }

    [Fact]
    public async Task The_launch_POST_sends_the_YouTube_origin()
    {
        var (client, http) = Build(HttpStatusCode.Created);

        await client.LaunchAppAsync(AppsUrl, "YouTube", "v=dQw4w9WgXcQ", CancellationToken.None);

        var sent = Assert.Single(http.Requests);
        Assert.Equal("POST", sent.Method);
        Assert.Equal(YouTubeOrigin, sent.Origin);
    }

    [Fact]
    public async Task An_app_with_no_known_origin_does_not_borrow_YouTubes()
    {
        // Claiming to be YouTube while calling something else would be a lie about
        // who is calling, so the header is per-app rather than blanket.
        var (client, http) = Build(HttpStatusCode.Created);

        await client.LaunchAppAsync(AppsUrl, "Netflix", "", CancellationToken.None);

        Assert.Null(Assert.Single(http.Requests).Origin);
    }

    [Fact]
    public void The_origin_lookup_is_case_insensitive_and_scoped_to_YouTube()
    {
        Assert.Equal(YouTubeOrigin, DialClient.OriginFor("youtube"));
        Assert.Equal(YouTubeOrigin, DialClient.OriginFor("YouTube"));
        Assert.Null(DialClient.OriginFor("Netflix"));
    }

    // ---- 403 is not "not installed" ------------------------------------------

    [Fact]
    public async Task A_403_app_status_is_a_TV_ERROR_not_a_missing_app()
    {
        var (client, _) = Build(HttpStatusCode.Forbidden);

        var error = await Assert.ThrowsAsync<TvException>(
            () => client.GetAppStatusAsync(AppsUrl, "YouTube", CancellationToken.None));

        Assert.Equal(TvErrorCode.TvError, error.Code);
        Assert.NotEqual(TvErrorCode.TvUnsupportedCapability, error.Code);
        Assert.Contains("403", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_403_app_status_says_the_app_is_probably_installed()
    {
        // The message has to point at authorisation, or the next person repeats
        // the "YouTube is not installed" wild goose chase.
        var (client, _) = Build(HttpStatusCode.Forbidden);

        var error = await Assert.ThrowsAsync<TvException>(
            () => client.GetAppStatusAsync(AppsUrl, "YouTube", CancellationToken.None));

        Assert.Contains("not a missing app", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_403_launch_is_a_TV_ERROR_naming_the_status()
    {
        var (client, _) = Build(HttpStatusCode.Forbidden);

        var error = await Assert.ThrowsAsync<TvException>(
            () => client.LaunchAppAsync(AppsUrl, "YouTube", "v=dQw4w9WgXcQ", CancellationToken.None));

        Assert.Equal(TvErrorCode.TvError, error.Code);
        Assert.Contains("403", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "503")]
    public async Task Any_non_404_refusal_names_its_status_rather_than_claiming_not_installed(
        HttpStatusCode status,
        string expected)
    {
        var (client, _) = Build(status);

        var error = await Assert.ThrowsAsync<TvException>(
            () => client.GetAppStatusAsync(AppsUrl, "YouTube", CancellationToken.None));

        Assert.Equal(TvErrorCode.TvError, error.Code);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    // ---- 404 still means not installed ---------------------------------------

    [Fact]
    public async Task A_404_app_status_still_means_not_installed()
    {
        // The one status that legitimately maps to null. Distinguishing 403 must
        // not disturb the genuine not-installed path.
        var (client, _) = Build(HttpStatusCode.NotFound);

        Assert.Null(await client.GetAppStatusAsync(AppsUrl, "YouTube", CancellationToken.None));
    }

    [Fact]
    public async Task A_200_app_status_is_parsed_as_before()
    {
        var (client, _) = Build(HttpStatusCode.OK, RunningBody);

        var status = await client.GetAppStatusAsync(AppsUrl, "YouTube", CancellationToken.None);

        Assert.True(status!.Installed);
        Assert.Equal("YouTube", status.Name);
    }
}

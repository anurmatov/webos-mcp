using System.Net;
using WebosMcp.Application;

namespace WebosMcp.Tests.Fakes;

/// <summary>
/// Answers only the exact URLs it was given, with the DIAL <c>Application-URL</c>
/// response header. Everything else 404s, so a test that expects a port to be
/// probed fails if the probe never happens.
/// </summary>
public sealed class StubDialHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, string> _applicationUrlByLocation;

    public StubDialHttpHandler(Dictionary<string, string> applicationUrlByLocation) =>
        _applicationUrlByLocation = new Dictionary<string, string>(applicationUrlByLocation, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every URL that was actually requested, in order.</summary>
    public List<string> Requested { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;

        lock (Requested)
        {
            Requested.Add(url);
        }

        if (!_applicationUrlByLocation.TryGetValue(url, out var applicationUrl))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("Application-URL", applicationUrl);
        return Task.FromResult(response);
    }
}

public sealed record RecordedRequest(string Method, string Url, string? Origin);

/// <summary>
/// Returns a scripted status for DIAL application calls and records what was sent,
/// so tests can assert both the response handling and the request headers.
/// </summary>
public sealed class ScriptedDialHttpHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;

    public ScriptedDialHttpHandler(HttpStatusCode status, string body = "")
    {
        _status = status;
        _body = body;
    }

    public List<RecordedRequest> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.TryGetValues("Origin", out var origin);

        Requests.Add(new RecordedRequest(
            request.Method.Method,
            request.RequestUri!.AbsoluteUri,
            origin?.FirstOrDefault()));

        return Task.FromResult(new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body),
        });
    }
}

/// <summary>
/// A scripted SSDP responder. Keyed by target endpoint so a test can give the
/// unicast address an answer while leaving multicast silent — which is exactly
/// the container case that broke DIAL resolution.
/// </summary>
public sealed class FakeSsdpChannel : ISsdpChannel
{
    private readonly Dictionary<string, IReadOnlyList<string>> _responsesByTarget;

    public FakeSsdpChannel(Dictionary<string, IReadOnlyList<string>>? responsesByTarget = null) =>
        _responsesByTarget = responsesByTarget ?? [];

    /// <summary>Every endpoint searched, in order, so tests can assert multicast was not required.</summary>
    public List<string> Searched { get; } = [];

    public Task<IReadOnlyList<string>> SearchAsync(
        IPEndPoint target,
        string searchTarget,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        Searched.Add(target.ToString());

        return Task.FromResult(_responsesByTarget.TryGetValue(target.ToString(), out var responses)
            ? responses
            : []);
    }

    public static string Notify(string location) =>
        "HTTP/1.1 200 OK\r\n" +
        "CACHE-CONTROL: max-age=1800\r\n" +
        "ST: urn:dial-multiscreen-org:service:dial:1\r\n" +
        $"LOCATION: {location}\r\n\r\n";
}

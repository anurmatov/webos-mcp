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
/// Records the full request — URL and form body — so the Lounge handshake's wire
/// shape can be asserted rather than assumed. The shape is what the receiver
/// actually validates, so nothing else proves it.
/// </summary>
public sealed class CapturingLoungeHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses;
    private readonly HttpStatusCode? _pollStatus;

    public CapturingLoungeHandler(params (HttpStatusCode Status, string Body)[] responses)
        : this(null, responses)
    {
    }

    /// <summary>
    /// <paramref name="pollStatus"/> forces the event poll's status, for asserting how
    /// a refused subscription is reported. Null serves it normally.
    /// </summary>
    public CapturingLoungeHandler(
        HttpStatusCode? pollStatus,
        params (HttpStatusCode Status, string Body)[] responses)
    {
        _pollStatus = pollStatus;
        _responses = new Queue<(HttpStatusCode, string)>(responses);
    }

    public List<(string Url, string Body, string? TokenHeader)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        request.Headers.TryGetValues("X-YouTube-LoungeId-Token", out var token);

        Requests.Add((request.RequestUri!.AbsoluteUri, body, token?.FirstOrDefault()));

        // The event poll is the only GET this client issues, and it is a LONG poll —
        // it must stay open with a read outstanding, not end immediately. Serving it
        // an empty body would make every subscription look like a stream that died
        // on contact, which is a different thing entirely.
        if (request.Method == HttpMethod.Get)
        {
            return new HttpResponseMessage(_pollStatus ?? HttpStatusCode.OK)
            {
                Content = new StreamContent(new HandoffStream()),
            };
        }

        var (status, responseBody) = _responses.Count > 0
            ? _responses.Dequeue()
            : (HttpStatusCode.OK, string.Empty);

        return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
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

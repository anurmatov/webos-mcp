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

/// <summary>How the scripted TV answers a DIAL status read on a given port.</summary>
public enum DialPortBehaviour
{
    Running,
    Refused,
    Hang,
    NotFound,
    Forbidden,
}

/// <summary>
/// A TV whose DIAL port can move, as it does after a reboot. Device-description
/// probes answer only on <see cref="CurrentPort"/>; a status read on any port
/// follows <see cref="StatusByPort"/> (default: running on the current port,
/// connection refused elsewhere).
/// </summary>
public sealed class MovingDialHttpHandler : HttpMessageHandler
{
    private readonly string _host;

    public MovingDialHttpHandler(string host, int currentPort)
    {
        _host = host;
        CurrentPort = currentPort;
    }

    public int CurrentPort { get; set; }

    public Dictionary<int, DialPortBehaviour> StatusByPort { get; } = [];

    /// <summary>Signalled when a status read starts hanging, so a test can cancel at the right moment.</summary>
    public TaskCompletionSource HangStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;

        if (uri.AbsolutePath == "/")
        {
            if (uri.Port != CurrentPort)
            {
                throw new HttpRequestException("Connection refused");
            }

            var probe = new HttpResponseMessage(HttpStatusCode.OK);
            probe.Headers.TryAddWithoutValidation("Application-URL", $"http://{_host}:{uri.Port}/apps/");
            return probe;
        }

        var behaviour = StatusByPort.TryGetValue(uri.Port, out var scripted)
            ? scripted
            : uri.Port == CurrentPort ? DialPortBehaviour.Running : DialPortBehaviour.Refused;

        switch (behaviour)
        {
            case DialPortBehaviour.Refused:
                throw new HttpRequestException("Connection refused");
            case DialPortBehaviour.Hang:
                HangStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("unreachable");
            case DialPortBehaviour.NotFound:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            case DialPortBehaviour.Forbidden:
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            default:
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<service xmlns=\"urn:dial-multiscreen-org:schemas:dial\"><name>YouTube</name>" +
                        "<state>running</state><additionalData><screenId>screen-1</screenId></additionalData></service>"),
                };
        }
    }
}

/// <summary>Counts resolutions on a real DIAL client, so a test can bound re-resolves per call.</summary>
public sealed class CountingDialClient(IDialClient inner) : IDialClient
{
    public int ResolveCount { get; private set; }

    public async Task<DialResolution?> ResolveApplicationUrlAsync(CancellationToken cancellationToken)
    {
        ResolveCount++;
        return await inner.ResolveApplicationUrlAsync(cancellationToken).ConfigureAwait(false);
    }

    public void InvalidateApplicationUrl(Uri stale) => inner.InvalidateApplicationUrl(stale);

    public Task<DialAppStatus?> GetAppStatusAsync(Uri applicationUrl, string app, CancellationToken cancellationToken) =>
        inner.GetAppStatusAsync(applicationUrl, app, cancellationToken);

    public Task<bool> LaunchAppAsync(Uri applicationUrl, string app, string payload, CancellationToken cancellationToken) =>
        inner.LaunchAppAsync(applicationUrl, app, payload, cancellationToken);
}

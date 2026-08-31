using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebosMcp.Application;
using WebosMcp.Domain;

namespace WebosMcp.Infrastructure;

/// <summary>
/// DIAL (DIscovery And Launch) client — the third protocol this server speaks,
/// after SSAP and Wake-on-LAN.
///
/// It exists for one reason: SSAP's launcher accepts a YouTube launch and
/// reports success while the TV stays on the home screen. DIAL provides a
/// launch whose outcome can be checked, and the caller additionally confirms
/// the app reached the foreground before reporting success.
/// </summary>
public sealed partial class DialClient : IDialClient, IDisposable
{
    private const string SsdpAddress = "239.255.255.250";
    private const int SsdpPort = 1900;
    private const string DialSearchTarget = "urn:dial-multiscreen-org:service:dial:1";

    private readonly HttpClient _http;
    private readonly WebosMcpOptions _options;
    private readonly ISsdpChannel _ssdp;
    private readonly ILogger<DialClient> _logger;

    // The application URL rarely changes, and rediscovery costs an SSDP round.
    private Uri? _cachedApplicationUrl;

    public DialClient(
        HttpClient http,
        IOptions<WebosMcpOptions> options,
        ISsdpChannel ssdp,
        ILogger<DialClient> logger)
    {
        _http = http;
        _options = options.Value;
        _ssdp = ssdp;
        _logger = logger;
    }

    [GeneratedRegex(@"^(?<name>[A-Za-z\-]+)\s*:\s*(?<value>.*)$", RegexOptions.Multiline)]
    private static partial Regex HeaderPattern();

    public async Task<Uri?> ResolveApplicationUrlAsync(CancellationToken cancellationToken)
    {
        if (_cachedApplicationUrl is not null)
        {
            return _cachedApplicationUrl;
        }

        // 1. An explicitly configured URL is authoritative — no discovery at all.
        var configured = _options.ResolvedDialApplicationUrl;
        if (configured is not null)
        {
            _logger.LogInformation("Using the configured DIAL application URL.");
            _cachedApplicationUrl = configured;
            return configured;
        }

        // 2. Probe the known host directly. This is the path that has to work in a
        //    container, where SSDP multicast does not leave the bridge network. The
        //    ports run concurrently so an absent DIAL endpoint costs one timeout,
        //    not one per port; the winner is still chosen in configured order so
        //    the result does not depend on which probe happened to return first.
        if (!string.IsNullOrWhiteSpace(_options.Host))
        {
            var ports = _options.ResolvedDialPorts;

            var probes = ports
                .Select(port => ProbeDeviceDescriptionAsync(
                    new Uri($"http://{_options.Host}:{port}/"), cancellationToken))
                .ToArray();

            var results = await Task.WhenAll(probes).ConfigureAwait(false);

            for (var i = 0; i < results.Length; i++)
            {
                if (results[i] is { } hit)
                {
                    _logger.LogInformation("Resolved DIAL on {Host}:{Port} by direct probe.", _options.Host, ports[i]);
                    _cachedApplicationUrl = hit;
                    return hit;
                }
            }
        }

        // 3. Unicast M-SEARCH straight at the TV. This needs no multicast route and
        //    returns the TV's own LOCATION, so it finds endpoints on ports or paths
        //    the direct probes above do not know about.
        var viaUnicast = await SearchAsync(UnicastSsdpTarget(), cancellationToken).ConfigureAwait(false);
        if (viaUnicast is not null)
        {
            _cachedApplicationUrl = viaUnicast;
            return viaUnicast;
        }

        // 4. Multicast, last. It is the one strategy a container usually cannot use.
        var viaMulticast = await SearchAsync(
            new IPEndPoint(IPAddress.Parse(SsdpAddress), SsdpPort), cancellationToken).ConfigureAwait(false);

        if (viaMulticast is null)
        {
            _logger.LogInformation(
                "No DIAL endpoint found: probed {Host} on ports {Ports}, then unicast and multicast SSDP.",
                _options.Host, _options.DialPorts);
        }

        _cachedApplicationUrl = viaMulticast;
        return viaMulticast;
    }

    /// <summary>
    /// The configured TV's SSDP port, or null when no usable host is configured.
    /// Resolution failures are not fatal here — this is one strategy of several.
    /// </summary>
    private IPEndPoint? UnicastSsdpTarget()
    {
        if (string.IsNullOrWhiteSpace(_options.Host))
        {
            return null;
        }

        if (IPAddress.TryParse(_options.Host, out var parsed))
        {
            return new IPEndPoint(parsed, SsdpPort);
        }

        try
        {
            var v4 = Array.Find(
                Dns.GetHostAddresses(_options.Host!),
                a => a.AddressFamily == AddressFamily.InterNetwork);

            return v4 is null ? null : new IPEndPoint(v4, SsdpPort);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Fetches a UPnP device description and reads the DIAL Application-URL
    /// response header. Returns null for anything that is not a DIAL endpoint.
    /// </summary>
    private async Task<Uri?> ProbeDeviceDescriptionAsync(Uri location, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            using var response = await _http
                .GetAsync(location, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            if (response.Headers.TryGetValues("Application-URL", out var values))
            {
                var raw = values.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(raw) &&
                    Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var appsUrl))
                {
                    _logger.LogInformation("Found DIAL application URL at {Url}.", appsUrl);
                    return appsUrl;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or UriFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs one M-SEARCH and probes every LOCATION it advertises. Returns the first
    /// address that answers as a real DIAL endpoint, or null.
    /// </summary>
    private async Task<Uri?> SearchAsync(IPEndPoint? target, CancellationToken cancellationToken)
    {
        if (target is null)
        {
            return null;
        }

        var window = TimeSpan.FromSeconds(Math.Max(1, _options.DialSsdpTimeoutSeconds));

        var responses = await _ssdp
            .SearchAsync(target, DialSearchTarget, window, cancellationToken)
            .ConfigureAwait(false);

        foreach (var response in responses)
        {
            if (!ParseHeaders(response).TryGetValue("LOCATION", out var location) ||
                !Uri.TryCreate(location, UriKind.Absolute, out var uri))
            {
                continue;
            }

            var appsUrl = await ProbeDeviceDescriptionAsync(uri, cancellationToken).ConfigureAwait(false);
            if (appsUrl is not null)
            {
                _logger.LogInformation("Resolved DIAL via SSDP search to {Target}.", target);
                return appsUrl;
            }
        }

        return null;
    }

    public async Task<DialAppStatus?> GetAppStatusAsync(
        Uri applicationUrl,
        string app,
        CancellationToken cancellationToken)
    {
        using var request = BuildRequest(HttpMethod.Get, applicationUrl, app);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // DIAL reports a not-installed app as 404. This is the ONLY status
                // that means "not installed".
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                // Anything else is the TV refusing or failing, not the app being
                // absent. Collapsing 403 into null told the caller "YouTube is not
                // installed" about a TV with YouTube installed, which sent the last
                // physical run chasing the wrong fault entirely.
                throw new TvException(TvErrorCode.TvError, DescribeRejection("status", app, response.StatusCode));
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseAppStatus(app, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            throw TvException.Unreachable($"Could not read DIAL status for '{app}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Names the HTTP status, and for 403 says what actually causes it. DIAL
    /// application endpoints are origin-checked: without the sender origin the
    /// app expects, the TV answers 403 whether or not the app is installed.
    /// </summary>
    private static string DescribeRejection(string operation, string app, HttpStatusCode code)
    {
        var detail = $"The TV rejected the DIAL {operation} request for '{app}' with HTTP {(int)code}.";

        return code == HttpStatusCode.Forbidden
            ? detail + " A DIAL 403 is an authorisation refusal, not a missing app — the app is most " +
              "likely installed but rejected the sender origin."
            : detail;
    }

    /// <summary>
    /// DIAL application requests are origin-checked. YouTube's DIAL endpoint
    /// refuses a sender that does not present its origin, so the header is sent
    /// on the application calls (status and launch) — never on the device
    /// description probes, which are not application requests.
    /// </summary>
    private static HttpRequestMessage BuildRequest(HttpMethod method, Uri applicationUrl, string app)
    {
        var request = new HttpRequestMessage(method, Combine(applicationUrl, app));

        if (OriginFor(app) is { } origin)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        return request;
    }

    /// <summary>
    /// The authorised sender origin for an app, or null when none is known. Kept
    /// per-app deliberately: sending YouTube's origin to some other app would be
    /// a lie about who is calling.
    /// </summary>
    internal static string? OriginFor(string app) =>
        app.Equals("YouTube", StringComparison.OrdinalIgnoreCase) ? "https://www.youtube.com" : null;

    internal static DialAppStatus? ParseAppStatus(string app, string body)
    {
        try
        {
            var document = XDocument.Parse(body);
            var state = document.Descendants()
                .FirstOrDefault(e => e.Name.LocalName.Equals("state", StringComparison.OrdinalIgnoreCase))
                ?.Value?.Trim();

            var name = document.Descendants()
                .FirstOrDefault(e => e.Name.LocalName.Equals("name", StringComparison.OrdinalIgnoreCase))
                ?.Value?.Trim();

            if (string.IsNullOrWhiteSpace(state))
            {
                return null;
            }

            // DIAL's "installable=..." state means the app is NOT installed.
            var installed = !state.StartsWith("installable", StringComparison.OrdinalIgnoreCase);

            // The receiver handle. DIAL's real job here is to hand over this id so
            // the YouTube Lounge session can be opened against the running receiver.
            var screenId = document.Descendants()
                .FirstOrDefault(e => e.Name.LocalName.Equals("screenId", StringComparison.OrdinalIgnoreCase))
                ?.Value?.Trim();

            return new DialAppStatus(
                name ?? app,
                state,
                installed,
                ScreenId: string.IsNullOrWhiteSpace(screenId) ? null : screenId);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    public async Task<bool> LaunchAppAsync(
        Uri applicationUrl,
        string app,
        string payload,
        CancellationToken cancellationToken)
    {
        using var request = BuildRequest(HttpMethod.Post, applicationUrl, app);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/x-www-form-urlencoded");

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            _logger.LogWarning(
                "DIAL launch of '{App}' was rejected with {Status}.", app, (int)response.StatusCode);

            // Throw rather than return false: the status is the diagnostic, and a
            // bare false discards it.
            throw new TvException(TvErrorCode.TvError, DescribeRejection("launch", app, response.StatusCode));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            throw TvException.Unreachable($"DIAL launch of '{app}' failed: {ex.Message}", ex);
        }
    }

    private static Uri Combine(Uri applicationUrl, string app)
    {
        var baseUrl = applicationUrl.AbsoluteUri.TrimEnd('/');
        return new Uri($"{baseUrl}/{Uri.EscapeDataString(app)}");
    }

    private static Dictionary<string, string> ParseHeaders(string response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in HeaderPattern().Matches(response))
        {
            headers[match.Groups["name"].Value.Trim()] = match.Groups["value"].Value.Trim();
        }

        return headers;
    }

    public void Dispose() => _http.Dispose();
}

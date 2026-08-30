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
    private readonly ILogger<DialClient> _logger;

    // The application URL rarely changes, and rediscovery costs an SSDP round.
    private Uri? _cachedApplicationUrl;

    public DialClient(HttpClient http, IOptions<WebosMcpOptions> options, ILogger<DialClient> logger)
    {
        _http = http;
        _options = options.Value;
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

        // Preferred: ask the configured TV directly. SSDP multicast is dropped
        // by plenty of networks, and we already know the address.
        if (!string.IsNullOrWhiteSpace(_options.Host))
        {
            foreach (var port in new[] { 1754, 3000, 8080, 9080 })
            {
                var candidate = await ProbeDeviceDescriptionAsync(
                    new Uri($"http://{_options.Host}:{port}/"), cancellationToken).ConfigureAwait(false);

                if (candidate is not null)
                {
                    _cachedApplicationUrl = candidate;
                    return candidate;
                }
            }
        }

        var located = await DiscoverViaSsdpAsync(cancellationToken).ConfigureAwait(false);
        _cachedApplicationUrl = located;
        return located;
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

    private async Task<Uri?> DiscoverViaSsdpAsync(CancellationToken cancellationToken)
    {
        var request =
            "M-SEARCH * HTTP/1.1\r\n" +
            $"HOST: {SsdpAddress}:{SsdpPort}\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n" +
            $"ST: {DialSearchTarget}\r\n\r\n";

        using var client = new UdpClient(AddressFamily.InterNetwork);
        client.EnableBroadcast = true;
        client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));

        try
        {
            await client.SendAsync(
                Encoding.ASCII.GetBytes(request),
                new IPEndPoint(IPAddress.Parse(SsdpAddress), SsdpPort),
                deadline.Token).ConfigureAwait(false);

            while (!deadline.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                var headers = ParseHeaders(Encoding.ASCII.GetString(result.Buffer));

                if (headers.TryGetValue("LOCATION", out var location) &&
                    Uri.TryCreate(location, UriKind.Absolute, out var uri))
                {
                    var appsUrl = await ProbeDeviceDescriptionAsync(uri, deadline.Token).ConfigureAwait(false);
                    if (appsUrl is not null)
                    {
                        return appsUrl;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Search window closed without a DIAL responder.
        }
        catch (SocketException ex)
        {
            _logger.LogWarning("DIAL SSDP discovery failed: {Message}", ex.Message);
        }

        _logger.LogInformation("No DIAL endpoint found on the local segment.");
        return null;
    }

    public async Task<DialAppStatus?> GetAppStatusAsync(
        Uri applicationUrl,
        string app,
        CancellationToken cancellationToken)
    {
        var target = Combine(applicationUrl, app);

        try
        {
            using var response = await _http.GetAsync(target, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // DIAL reports a not-installed app as 404.
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseAppStatus(app, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            throw TvException.Unreachable($"Could not read DIAL status for '{app}': {ex.Message}", ex);
        }
    }

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

            return new DialAppStatus(name ?? app, state, installed);
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
        var target = Combine(applicationUrl, app);

        using var content = new StringContent(payload, Encoding.UTF8, "application/x-www-form-urlencoded");

        try
        {
            using var response = await _http.PostAsync(target, content, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            _logger.LogWarning(
                "DIAL launch of '{App}' was rejected with {Status}.", app, (int)response.StatusCode);
            return false;
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

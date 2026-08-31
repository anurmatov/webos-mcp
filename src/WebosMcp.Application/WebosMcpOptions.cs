using System.Net;
using System.Net.NetworkInformation;
using WebosMcp.Domain;

namespace WebosMcp.Application;

/// <summary>
/// Configuration for the single TV this instance controls. V1 is deliberately
/// single-TV: there is no device registry and no per-call routing.
/// </summary>
public sealed class WebosMcpOptions
{
    public const string SectionName = "WebosMcp";

    /// <summary>TV hostname or IP, e.g. the documentation-only address 192.0.2.10.</summary>
    public string? Host { get; set; }

    /// <summary>SSAP port. 3000 plaintext, 3001 TLS.</summary>
    public int Port { get; set; } = 3000;

    public bool UseTls { get; set; }

    /// <summary>TV MAC address for Wake-on-LAN, e.g. 00:11:22:33:44:55.</summary>
    public string? MacAddress { get; set; }

    public const string DefaultBroadcastAddress = "255.255.255.255";

    /// <summary>Broadcast address used for the WOL magic packet. Defaults to the all-subnets broadcast.</summary>
    public string BroadcastAddress { get; set; } = DefaultBroadcastAddress;

    /// <summary>
    /// Where the device book lives. Registration writes here, so in a container it
    /// must be a writable volume — the same requirement as the client key.
    /// </summary>
    public string? DeviceStorePath { get; set; }

    /// <summary>
    /// True once <see cref="Host"/> was supplied by the device book rather than by
    /// the operator. Explicit environment configuration always wins; this flag is
    /// what stops a stored selection silently overriding it.
    /// </summary>
    public bool HostCameFromDeviceBook { get; set; }

    /// <summary>
    /// Explicit DIAL application URL, e.g. http://192.0.2.10:2038/apps/. When set,
    /// DIAL resolution uses it directly and performs no discovery at all — the
    /// deterministic escape hatch for networks where neither the direct port
    /// probes nor SSDP reach the TV.
    /// </summary>
    public string? DialApplicationUrl { get; set; }

    /// <summary>
    /// Comma-separated ports probed directly on <see cref="Host"/> when looking for
    /// the TV's DIAL device description. 2038 is first because that is the port LG
    /// webOS was observed advertising; a container cannot rely on SSDP multicast to
    /// find it, so the known host is probed directly before any discovery is tried.
    /// </summary>
    public string DialPorts { get; set; } = "2038,1754,3000,8080,9080";

    /// <summary>How long a single SSDP M-SEARCH window stays open.</summary>
    public int DialSsdpTimeoutSeconds { get; set; } = 3;

    /// <summary>
    /// YouTube Lounge service base URL. This is the ONE part of this server that
    /// leaves the LAN: controlling an already-running YouTube receiver requires
    /// Google's Lounge service, because DIAL cannot select a video in a running
    /// session nor report which video is playing.
    /// </summary>
    public string LoungeBaseUrl { get; set; } = "https://www.youtube.com";

    /// <summary>Name this remote presents to the receiver.</summary>
    public string LoungeDeviceName { get; set; } = "webos-mcp";

    /// <summary>
    /// How long to wait for the receiver to report the requested video actually
    /// playing. Expiring returns a failure naming what WAS observed — never an
    /// unverified success.
    /// </summary>
    public int LoungeVerifyTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// How long to wait for the receiver's event stream to start BEING READ before
    /// any command is sent. Readiness is a read outstanding on the stream, not
    /// response headers coming back — the receiver announces a state change once, to
    /// whoever is listening at that instant, so headers alone leave the announcement
    /// with nowhere to land.
    ///
    /// Its own bound, separate from the verification budget: the two failures are
    /// different and must be reported differently — a stream that never started
    /// delivering means nothing was attempted, whereas an expired verification budget
    /// means the command went out and was never confirmed.
    /// </summary>
    public int LoungeSubscribeTimeoutSeconds { get; set; } = 10;

    /// <summary>Pre-paired client key supplied inline (environment variable).</summary>
    public string? ClientKey { get; set; }

    /// <summary>Path to a file containing the client key (mounted secret).</summary>
    public string? ClientKeyFile { get; set; }

    /// <summary>
    /// The durable, WRITABLE location pairing persists the key to. This is
    /// distinct from <see cref="ClientKeyFile"/>, which is a read-only mounted
    /// secret: a container typically reads from the mount and must write here.
    /// </summary>
    public string? ClientKeyPath { get; set; }

    /// <summary>
    /// Opt-in for the <c>pair_device</c> MCP tool. Defaults to FALSE, so a
    /// default deployment exposes no pairing surface at all — the tool is not
    /// merely refused, it is never registered and never appears in tools/list.
    /// Pairing still requires a human to accept the prompt on the TV.
    /// </summary>
    public bool EnablePairingTool { get; set; }

    /// <summary>
    /// How long to wait for a human to accept the on-screen pairing prompt.
    /// Deliberately much longer than <see cref="RequestTimeoutSeconds"/> —
    /// someone has to physically reach the TV.
    /// </summary>
    public int PairingTimeoutSeconds { get; set; } = 60;

    public int ConnectTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Bound on the screenshot download, separate from
    /// <see cref="RequestTimeoutSeconds"/> because it covers a different hop: the
    /// SSAP call that produces the URI has already completed by then, and the
    /// download runs outside the SSAP session so a slow fetch cannot hold the
    /// control channel's gate.
    ///
    /// Read through <see cref="ResolvedScreenshotTimeoutSeconds"/>, never directly:
    /// the raw value is operator input and an unchecked 0 or -1 would turn a bound
    /// into its absence.
    /// </summary>
    public int ScreenshotTimeoutSeconds { get; set; } = DefaultScreenshotTimeoutSeconds;

    /// <summary>
    /// Hard cap on a captured frame. The download is streamed and aborted the
    /// moment this is exceeded — the body is never buffered unbounded. A panel
    /// frame is a few hundred kilobytes, so the default is deliberately generous
    /// while still bounded.
    ///
    /// Read through <see cref="ResolvedScreenshotMaxBytes"/>, never directly.
    /// </summary>
    public int ScreenshotMaxBytes { get; set; } = DefaultScreenshotMaxBytes;

    public const int DefaultScreenshotTimeoutSeconds = 15;
    public const int DefaultScreenshotMaxBytes = 8 * 1024 * 1024;

    /// <summary>Below this a capture could not complete on any real network.</summary>
    public const int MinScreenshotTimeoutSeconds = 1;

    /// <summary>Five minutes. Past this the "bounded timeout" guarantee is nominal.</summary>
    public const int MaxScreenshotTimeoutSeconds = 300;

    /// <summary>No real image is smaller than this, so a lower cap can only reject captures.</summary>
    public const int MinScreenshotMaxBytes = 1024;

    /// <summary>
    /// 64 MiB. The whole body is held in memory while it is validated, so the
    /// ceiling is what stops a hostile or broken responder turning the cap into an
    /// out-of-memory condition.
    /// </summary>
    public const int MaxScreenshotMaxBytes = 64 * 1024 * 1024;

    /// <summary>
    /// The download timeout, range-checked. Follows <see cref="ResolvedDialPorts"/>:
    /// an out-of-range value is an operator error and is reported as one rather
    /// than clamped, because silently substituting a different bound than the one
    /// configured is how a limit stops meaning what its owner thinks it means.
    /// </summary>
    public int ResolvedScreenshotTimeoutSeconds => RequireInRange(
        ScreenshotTimeoutSeconds,
        MinScreenshotTimeoutSeconds,
        MaxScreenshotTimeoutSeconds,
        "WEBOSMCP__SCREENSHOTTIMEOUTSECONDS",
        "seconds");

    /// <summary>The maximum capture size, range-checked. See above.</summary>
    public int ResolvedScreenshotMaxBytes => RequireInRange(
        ScreenshotMaxBytes,
        MinScreenshotMaxBytes,
        MaxScreenshotMaxBytes,
        "WEBOSMCP__SCREENSHOTMAXBYTES",
        "bytes");

    /// <summary>
    /// Touches every range-checked screenshot setting so a bad value fails the
    /// server at startup rather than the first capture. Returns the first problem,
    /// or null when the configuration is usable.
    /// </summary>
    public string? ValidateScreenshotLimits()
    {
        try
        {
            _ = ResolvedScreenshotTimeoutSeconds;
            _ = ResolvedScreenshotMaxBytes;
            return null;
        }
        catch (TvException ex)
        {
            return ex.Message;
        }
    }

    private static int RequireInRange(int value, int min, int max, string key, string unit)
    {
        if (value < min || value > max)
        {
            throw TvException.Invalid(
                $"{key} is {value} {unit}, which is outside the accepted range {min}-{max}. " +
                "A zero, negative or unbounded value would remove the limit rather than configure it.");
        }

        return value;
    }

    /// <summary>
    /// How long to wait for an app launched over DIAL to actually reach the
    /// foreground. A launch that is accepted but never appears is a failure,
    /// not a slow success.
    /// </summary>
    public int LaunchVerifyTimeoutSeconds { get; set; } = 20;

    public int LaunchPollIntervalSeconds { get; set; } = 2;
    public int RequestTimeoutSeconds { get; set; } = 15;

    /// <summary>How long <c>power_on</c> polls for an Active state before returning an unverified result.</summary>
    public int PowerOnVerifyTimeoutSeconds { get; set; } = 60;

    public int PowerOnPollIntervalSeconds { get; set; } = 3;

    /// <summary>Delay between steps of a bounded remote-control fallback sequence.</summary>
    public int FallbackStepDelayMilliseconds { get; set; } = 400;

    /// <summary>
    /// <see cref="DialPorts"/> parsed, de-duplicated and order-preserving. Invalid or
    /// out-of-range entries are rejected loudly rather than silently skipped: a typo
    /// that quietly drops the one port the TV answers on is exactly the failure this
    /// setting exists to prevent.
    /// </summary>
    public IReadOnlyList<int> ResolvedDialPorts
    {
        get
        {
            var ports = new List<int>();

            foreach (var part in (DialPorts ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(part, out var port) || port < 1 || port > 65535)
                {
                    throw TvException.Invalid(
                        $"'{part}' in WEBOSMCP__DIALPORTS is not a valid TCP port. Expected a comma-separated list, for example 2038,1754,3000.");
                }

                if (!ports.Contains(port))
                {
                    ports.Add(port);
                }
            }

            return ports;
        }
    }

    /// <summary>
    /// <see cref="DialApplicationUrl"/> validated, or null when unset. A malformed
    /// value is an operator error and is reported as one — never degraded into
    /// "this TV has no DIAL endpoint", which would blame the TV for a typo.
    /// </summary>
    public Uri? ResolvedDialApplicationUrl
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DialApplicationUrl))
            {
                return null;
            }

            if (!Uri.TryCreate(DialApplicationUrl.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw TvException.Invalid(
                    $"WEBOSMCP__DIALAPPLICATIONURL ('{DialApplicationUrl}') is not an absolute http(s) URL, for example http://192.0.2.10:2038/apps/.");
            }

            return uri;
        }
    }

    public string ResolvedDeviceStorePath =>
        string.IsNullOrWhiteSpace(DeviceStorePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".webos-mcp",
                "devices.json")
            : DeviceStorePath!;

    public string ResolvedClientKeyPath =>
        string.IsNullOrWhiteSpace(ClientKeyPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".webos-mcp",
                "clientkey.json")
            : ClientKeyPath!;

    public IPEndPoint RequireEndpoint()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw TvException.Invalid(
                "No TV host is configured. Set WEBOSMCP__HOST (for example 192.0.2.10).");
        }

        return new IPEndPoint(ResolveHost(Host!), Port);
    }

    public PhysicalAddress RequireMac()
    {
        if (string.IsNullOrWhiteSpace(MacAddress))
        {
            throw TvException.Invalid(
                "No TV MAC address is configured. Set WEBOSMCP__MACADDRESS (for example 00:11:22:33:44:55) to enable Wake-on-LAN.");
        }

        return ParseMac(MacAddress!);
    }

    internal static IPAddress ResolveHost(string host)
    {
        if (IPAddress.TryParse(host, out var parsed))
        {
            return parsed;
        }

        try
        {
            var addresses = Dns.GetHostAddresses(host);
            var v4 = Array.Find(addresses, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            return v4 ?? addresses.FirstOrDefault()
                ?? throw TvException.Unreachable($"Host '{host}' did not resolve to any address.");
        }
        catch (Exception ex) when (ex is not TvException)
        {
            throw TvException.Unreachable($"Host '{host}' could not be resolved.", ex);
        }
    }

    public static PhysicalAddress ParseMac(string mac)
    {
        var normalised = mac.Replace(":", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Replace(".", "", StringComparison.Ordinal)
            .Trim();

        if (normalised.Length != 12 || !normalised.All(Uri.IsHexDigit))
        {
            throw TvException.Invalid(
                $"'{mac}' is not a valid MAC address. Expected 6 hex octets, for example 00:11:22:33:44:55.");
        }

        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            bytes[i] = Convert.ToByte(normalised.Substring(i * 2, 2), 16);
        }

        return new PhysicalAddress(bytes);
    }
}

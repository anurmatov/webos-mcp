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

    /// <summary>Broadcast address used for the WOL magic packet. Defaults to the all-subnets broadcast.</summary>
    public string BroadcastAddress { get; set; } = "255.255.255.255";

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
    public int RequestTimeoutSeconds { get; set; } = 15;

    /// <summary>How long <c>power_on</c> polls for an Active state before returning an unverified result.</summary>
    public int PowerOnVerifyTimeoutSeconds { get; set; } = 60;

    public int PowerOnPollIntervalSeconds { get; set; } = 3;

    /// <summary>Delay between steps of a bounded remote-control fallback sequence.</summary>
    public int FallbackStepDelayMilliseconds { get; set; } = 400;

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

    internal static PhysicalAddress ParseMac(string mac)
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

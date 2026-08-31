using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WebosMcp.Application;

namespace WebosMcp.Infrastructure;

/// <summary>
/// SSDP M-SEARCH discovery for LG webOS TVs on the local segment. Used only by
/// the operator <c>discover</c> command — never reachable as an MCP tool.
/// </summary>
public sealed partial class SsdpTvDiscovery : ITvDiscovery
{
    private const string MulticastAddress = "239.255.255.250";
    private const int MulticastPort = 1900;
    private const string SearchTarget = "urn:lge-com:service:webos-second-screen:1";

    private readonly ILogger<SsdpTvDiscovery> _logger;

    public SsdpTvDiscovery(ILogger<SsdpTvDiscovery> logger) => _logger = logger;

    [GeneratedRegex(@"^(?<name>[A-Za-z\-]+)\s*:\s*(?<value>.*)$", RegexOptions.Multiline)]
    private static partial Regex HeaderPattern();

    public async Task<IReadOnlyList<DiscoveredTv>> DiscoverAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var request =
            "M-SEARCH * HTTP/1.1\r\n" +
            $"HOST: {MulticastAddress}:{MulticastPort}\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 3\r\n" +
            $"ST: {SearchTarget}\r\n\r\n";

        var found = new Dictionary<string, DiscoveredTv>(StringComparer.OrdinalIgnoreCase);

        using var client = new UdpClient(AddressFamily.InterNetwork);
        client.EnableBroadcast = true;
        client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var target = new IPEndPoint(IPAddress.Parse(MulticastAddress), MulticastPort);
        var payload = Encoding.ASCII.GetBytes(request);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            await client.SendAsync(payload, target, deadline.Token).ConfigureAwait(false);

            while (!deadline.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                var text = Encoding.ASCII.GetString(result.Buffer);
                var headers = ParseHeaders(text);

                if (!headers.TryGetValue("LOCATION", out var location))
                {
                    continue;
                }

                var address = result.RemoteEndPoint.Address.ToString();
                headers.TryGetValue("DLNADeviceName.lge.com", out var friendly);
                headers.TryGetValue("SERVER", out var server);

                found[address] = new DiscoveredTv(
                    address,
                    friendly is null ? null : Uri.UnescapeDataString(friendly),
                    server ?? location);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected — the search window closed.
        }
        catch (SocketException ex)
        {
            _logger.LogWarning("SSDP discovery failed: {Message}", ex.Message);
        }

        return [.. found.Values];
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
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using WebosMcp.Application;

namespace WebosMcp.Infrastructure;

/// <summary>
/// The real UDP transport behind <see cref="ISsdpChannel"/>. It does not care
/// whether the target is the multicast group or a single TV address, which is
/// what lets DIAL resolution fall back to a unicast M-SEARCH when multicast is
/// unavailable — the usual case inside a bridge-mode container.
/// </summary>
public sealed class UdpSsdpChannel : ISsdpChannel
{
    private readonly ILogger<UdpSsdpChannel> _logger;

    public UdpSsdpChannel(ILogger<UdpSsdpChannel> logger) => _logger = logger;

    public async Task<IReadOnlyList<string>> SearchAsync(
        IPEndPoint target,
        string searchTarget,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        // MX must be <= the window, or a compliant device may answer after we stop listening.
        var mx = Math.Max(1, (int)Math.Floor(window.TotalSeconds) - 1);

        var request =
            "M-SEARCH * HTTP/1.1\r\n" +
            $"HOST: {target}\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            $"MX: {mx}\r\n" +
            $"ST: {searchTarget}\r\n\r\n";

        var responses = new List<string>();

        using var client = new UdpClient(AddressFamily.InterNetwork);
        client.EnableBroadcast = true;
        client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(window);

        try
        {
            await client.SendAsync(Encoding.ASCII.GetBytes(request), target, deadline.Token).ConfigureAwait(false);

            while (!deadline.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                responses.Add(Encoding.ASCII.GetString(result.Buffer));
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the search window closed. Whatever arrived is the answer.
        }
        catch (SocketException ex)
        {
            // A container with no multicast route fails here rather than timing out.
            // Not fatal — the caller has other resolution strategies.
            _logger.LogDebug("SSDP search to {Target} failed: {Message}", target, ex.Message);
        }

        return responses;
    }
}

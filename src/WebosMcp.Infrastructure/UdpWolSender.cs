using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using WebosMcp.Application;

namespace WebosMcp.Infrastructure;

/// <summary>
/// Sends the standard 102-byte Wake-on-LAN magic packet to every supplied
/// target. A failure on one target does not abort the others — the unicast
/// fallback is precisely for the case where the broadcast leg cannot escape a
/// Docker bridge network.
/// </summary>
public sealed class UdpWolSender : IWolSender
{
    private readonly ILogger<UdpWolSender> _logger;

    public UdpWolSender(ILogger<UdpWolSender> logger) => _logger = logger;

    public async Task<IReadOnlyList<string>> SendAsync(
        PhysicalAddress mac,
        IReadOnlyList<IPEndPoint> targets,
        CancellationToken cancellationToken)
    {
        var packet = BuildMagicPacket(mac);
        var delivered = new List<string>();

        foreach (var target in targets)
        {
            try
            {
                using var client = new UdpClient(AddressFamily.InterNetwork);
                client.EnableBroadcast = true;
                await client.SendAsync(packet, target, cancellationToken).ConfigureAwait(false);
                delivered.Add(target.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Wake-on-LAN send to {Target} failed: {Message}", target, ex.Message);
            }
        }

        return delivered;
    }

    internal static byte[] BuildMagicPacket(PhysicalAddress mac)
    {
        var address = mac.GetAddressBytes();
        if (address.Length != 6)
        {
            throw new ArgumentException("A Wake-on-LAN MAC address must be 6 bytes.", nameof(mac));
        }

        var packet = new byte[6 + (16 * 6)];
        for (var i = 0; i < 6; i++)
        {
            packet[i] = 0xFF;
        }

        for (var repetition = 0; repetition < 16; repetition++)
        {
            Buffer.BlockCopy(address, 0, packet, 6 + (repetition * 6), 6);
        }

        return packet;
    }
}

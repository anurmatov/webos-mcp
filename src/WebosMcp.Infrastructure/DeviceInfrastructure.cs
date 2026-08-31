using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebosMcp.Application;
using WebosMcp.Domain;

namespace WebosMcp.Infrastructure;

/// <summary>
/// Stores the device book as JSON beside the client key. Written atomically, and
/// deliberately holds no secret: the pairing key lives in its own file with its own
/// permissions.
/// </summary>
public sealed class FileDeviceStore : IDeviceStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly WebosMcpOptions _options;
    private readonly ILogger<FileDeviceStore> _logger;

    public FileDeviceStore(IOptions<WebosMcpOptions> options, ILogger<FileDeviceStore> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public string DescribeLocation() => _options.ResolvedDeviceStorePath;

    public async Task<DeviceBook> LoadAsync(CancellationToken cancellationToken)
    {
        var path = _options.ResolvedDeviceStorePath;

        if (!File.Exists(path))
        {
            return DeviceBook.Empty;
        }

        try
        {
            await using var stream = File.OpenRead(path);

            return await JsonSerializer
                .DeserializeAsync<DeviceBook>(stream, Json, cancellationToken)
                .ConfigureAwait(false) ?? DeviceBook.Empty;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt book must not brick the server — explicit environment
            // configuration may still be enough to run.
            _logger.LogWarning("The device book at {Path} could not be read: {Message}", path, ex.Message);
            return DeviceBook.Empty;
        }
    }

    public async Task SaveAsync(DeviceBook book, CancellationToken cancellationToken)
    {
        var path = _options.ResolvedDeviceStorePath;
        var directory = Path.GetDirectoryName(path);

        try
        {
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Temp-then-rename: a crash mid-write leaves the previous book intact
            // rather than a truncated one.
            var temp = path + ".tmp";

            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, book, Json, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw TvException.KeyStorageReadOnly(
                $"The device book location '{path}' is not writable: {ex.Message}. " +
                "Set WEBOSMCP__DEVICESTOREPATH to a writable location, or mount a writable volume.");
        }
    }
}

/// <summary>
/// Derives address details from the host so an operator does not type them.
/// Everything here is best-effort: a null return means "could not determine", which
/// callers surface rather than guessing at.
/// </summary>
public sealed class SystemNetworkFacts : INetworkFacts
{
    private readonly ILogger<SystemNetworkFacts> _logger;

    public SystemNetworkFacts(ILogger<SystemNetworkFacts> logger) => _logger = logger;

    public string? TryGetMacAddress(string host)
    {
        if (!IPAddress.TryParse(host, out var address))
        {
            return null;
        }

        try
        {
            // Prime the neighbour table: an address never spoken to has no entry.
            using (var ping = new Ping())
            {
                ping.Send(address, 500);
            }

            return ReadNeighbourTable(address.ToString());
        }
        catch (Exception ex) when (ex is PingException or SocketException or InvalidOperationException)
        {
            _logger.LogDebug("Could not derive a MAC address for {Host}: {Message}", host, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Reads the OS neighbour table. Parsed from command output because .NET exposes
    /// no cross-platform ARP API; failure is expected in a container without the tool
    /// and simply yields null.
    /// </summary>
    private string? ReadNeighbourTable(string address)
    {
        foreach (var (file, args) in new[] { ("ip", "neigh"), ("arp", "-n") })
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(file, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });

                if (process is null)
                {
                    continue;
                }

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(2000);

                if (ParseMacForAddress(output, address) is { } mac)
                {
                    return mac;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Tool absent — try the next one.
            }
        }

        return null;
    }

    internal static string? ParseMacForAddress(string output, string address)
    {
        foreach (var line in output.Split('\n'))
        {
            // Match the address as a whole field, so 192.0.2.1 does not match
            // 192.0.2.10.
            var fields = line.Split([' ', '\t', '(', ')'], StringSplitOptions.RemoveEmptyEntries);

            if (!fields.Contains(address, StringComparer.Ordinal))
            {
                continue;
            }

            foreach (var field in fields)
            {
                if (field.Count(c => c == ':') == 5 && field.Length == 17)
                {
                    return field.ToLowerInvariant();
                }
            }
        }

        return null;
    }

    public string? TryGetBroadcastAddress(string host)
    {
        if (!IPAddress.TryParse(host, out var target) ||
            target.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                        unicast.IPv4Mask is null)
                    {
                        continue;
                    }

                    if (Broadcast(unicast.Address, unicast.IPv4Mask) is { } broadcast &&
                        SameSubnet(unicast.Address, target, unicast.IPv4Mask))
                    {
                        return broadcast;
                    }
                }
            }
        }
        catch (NetworkInformationException ex)
        {
            _logger.LogDebug("Could not derive a broadcast address for {Host}: {Message}", host, ex.Message);
        }

        return null;
    }

    internal static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        var left = a.GetAddressBytes();
        var right = b.GetAddressBytes();
        var bits = mask.GetAddressBytes();

        if (left.Length != 4 || right.Length != 4 || bits.Length != 4)
        {
            return false;
        }

        for (var i = 0; i < 4; i++)
        {
            if ((left[i] & bits[i]) != (right[i] & bits[i]))
            {
                return false;
            }
        }

        return true;
    }

    internal static string? Broadcast(IPAddress address, IPAddress mask)
    {
        var bytes = address.GetAddressBytes();
        var bits = mask.GetAddressBytes();

        if (bytes.Length != 4 || bits.Length != 4)
        {
            return null;
        }

        for (var i = 0; i < 4; i++)
        {
            bytes[i] = (byte)(bytes[i] | (byte)~bits[i]);
        }

        return new IPAddress(bytes).ToString();
    }
}

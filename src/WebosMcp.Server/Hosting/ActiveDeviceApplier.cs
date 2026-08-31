using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebosMcp.Application;

namespace WebosMcp.Server.Hosting;

/// <summary>
/// Applies the stored active device to the running configuration at startup, so a
/// device registered through MCP is still in effect after a restart. Explicit
/// environment configuration still wins — see <see cref="DeviceService.ApplyActiveAsync"/>.
/// </summary>
public sealed class ActiveDeviceApplier : IHostedService
{
    private readonly DeviceService _devices;
    private readonly ILogger<ActiveDeviceApplier> _logger;

    public ActiveDeviceApplier(DeviceService devices, ILogger<ActiveDeviceApplier> logger)
    {
        _devices = devices;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var active = await _devices.ApplyActiveAsync(cancellationToken).ConfigureAwait(false);

        if (active is not null)
        {
            // No identifier, address or name. The fact that a stored selection was
            // applied is what an operator needs at startup; WHICH device it was is
            // something they can ask for with tv_list_devices, and putting it in a
            // log line writes a device identifier into every log sink for the life
            // of the process to answer a question nobody asked.
            _logger.LogInformation("Applied the stored active TV selection.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

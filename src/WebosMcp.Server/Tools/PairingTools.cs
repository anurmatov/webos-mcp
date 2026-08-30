using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WebosMcp.Infrastructure;

namespace WebosMcp.Server.Tools;

/// <summary>
/// The opt-in pairing surface.
///
/// NOTE the deliberate absence of <c>[McpServerToolType]</c>: that keeps this
/// class invisible to <c>WithToolsFromAssembly</c>, so on a default deployment
/// the tool is not registered at all and never appears in <c>tools/list</c>.
/// It is added explicitly, and only when <c>EnablePairingTool</c> is true — the
/// default-off boundary is structural rather than a runtime refusal.
/// </summary>
public sealed class PairingTools
{
    private readonly PairingService _pairing;
    private readonly ILogger<PairingTools> _logger;

    public PairingTools(PairingService pairing, ILogger<PairingTools> logger)
    {
        _pairing = pairing;
        _logger = logger;
    }

    [McpServerTool(Name = "pair_device")]
    [Description(
        "Pair with the configured TV. A human must accept the on-screen prompt — this cannot pair unattended. " +
        "The client key is persisted atomically and verified on disk before success is reported, and is never " +
        "returned by this tool. Only the storage location is reported.")]
    public Task<ToolResult> PairDevice(
        [Description("Re-pair even if a working key is already stored. Raises a new on-screen prompt.")]
        bool force = false,
        CancellationToken cancellationToken = default) =>
        ToolInvoker.RunAsync(_logger, "pair_device", async () =>
        {
            var outcome = await _pairing.PairAsync(force, cancellationToken).ConfigureAwait(false);

            // Location and status only. The key never crosses this boundary.
            return new
            {
                status = outcome.AlreadyPaired ? "already_paired" : "paired",
                alreadyPaired = outcome.AlreadyPaired,
                verifiedOnDisk = outcome.VerifiedOnDisk,
                location = outcome.Location,
                detail = outcome.AlreadyPaired
                    ? "A working client key was already stored; no on-screen prompt was raised."
                    : "Pairing accepted on the TV. The client key was persisted and verified on disk.",
            };
        });
}

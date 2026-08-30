using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebosMcp.Domain;

namespace WebosMcp.Application;

/// <summary>
/// Wake-on-LAN power-on with post-WOL verification. Never reports an
/// optimistic success: either the TV was observed reaching an Active-equivalent
/// state, or the result is explicitly labelled unverified.
/// </summary>
public sealed class PowerService
{
    /// <summary>Standard WOL port. 7 and 40000 are also seen; 9 is the common default.</summary>
    public const int WolPort = 9;

    private readonly IWolSender _wol;
    private readonly TvControlService _control;
    private readonly IDelayProvider _delay;
    private readonly WebosMcpOptions _options;
    private readonly ILogger<PowerService> _logger;

    public PowerService(
        IWolSender wol,
        TvControlService control,
        IDelayProvider delay,
        IOptions<WebosMcpOptions> options,
        ILogger<PowerService> logger)
    {
        _wol = wol;
        _control = control;
        _delay = delay;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PowerOnResult> PowerOnAsync(CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();

        // Idempotency: an already-Active TV is a safe no-op that still returns a
        // verified state rather than firing a redundant packet.
        var initial = await ProbeAsync(ct).ConfigureAwait(false);
        if (initial == PowerState.Active)
        {
            return new PowerOnResult(
                Verified: true,
                FinalState: PowerState.Active,
                AlreadyOn: true,
                MagicPacketsSent: 0,
                SentTo: [],
                ElapsedSeconds: Elapsed(started),
                Detail: "TV was already Active; no magic packet was sent.");
        }

        var mac = _options.RequireMac();
        var targets = BuildTargets();
        var sentTo = await _wol.SendAsync(mac, targets, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Sent {Count} Wake-on-LAN magic packet(s) to {Targets}.", sentTo.Count, string.Join(", ", sentTo));

        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.PowerOnPollIntervalSeconds));
        var budget = TimeSpan.FromSeconds(Math.Max(1, _options.PowerOnVerifyTimeoutSeconds));

        // Bounded by BOTH an attempt count and wall-clock, so the loop is
        // deterministic under a fake delay provider and still honours the
        // configured timeout in production.
        var maxAttempts = (int)Math.Ceiling(budget.TotalSeconds / interval.TotalSeconds);
        var state = initial;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            await _delay.DelayAsync(interval, ct).ConfigureAwait(false);

            state = await ProbeAsync(ct).ConfigureAwait(false);
            if (state == PowerState.Active)
            {
                return new PowerOnResult(
                    Verified: true,
                    FinalState: state,
                    AlreadyOn: false,
                    MagicPacketsSent: sentTo.Count,
                    SentTo: sentTo,
                    ElapsedSeconds: Elapsed(started),
                    Detail: "Magic packet sent and the TV was observed reaching an Active state.");
            }

            if (Elapsed(started) >= budget.TotalSeconds)
            {
                break;
            }
        }

        return new PowerOnResult(
            Verified: false,
            FinalState: state,
            AlreadyOn: false,
            MagicPacketsSent: sentTo.Count,
            SentTo: sentTo,
            ElapsedSeconds: Elapsed(started),
            Detail:
                $"Magic packet sent to {sentTo.Count} target(s), but the TV did not reach an Active state within " +
                $"{budget.TotalSeconds:0}s. This result is UNVERIFIED — the packet may not have reached the TV " +
                "(see the README note on bridge-mode Docker networking), or the TV may not have Wake-on-LAN enabled.");
    }

    /// <summary>
    /// Subnet broadcast plus a directed unicast to the TV's last-known address.
    /// The unicast leg is the documented best-effort fallback for bridge-mode
    /// container deployments, where Docker's NAT does not forward the broadcast
    /// out to the physical LAN. It is hardware-dependent, not guaranteed.
    /// </summary>
    internal IReadOnlyList<IPEndPoint> BuildTargets()
    {
        var targets = new List<IPEndPoint>();

        if (IPAddress.TryParse(_options.BroadcastAddress, out var broadcast))
        {
            targets.Add(new IPEndPoint(broadcast, WolPort));
        }
        else
        {
            targets.Add(new IPEndPoint(IPAddress.Broadcast, WolPort));
        }

        if (!string.IsNullOrWhiteSpace(_options.Host))
        {
            try
            {
                var unicast = new IPEndPoint(WebosMcpOptions.ResolveHost(_options.Host!), WolPort);
                if (!targets.Any(t => t.Equals(unicast)))
                {
                    targets.Add(unicast);
                }
            }
            catch (TvException ex)
            {
                // A sleeping TV can drop out of DNS/ARP; the broadcast leg still stands.
                _logger.LogDebug("Skipping unicast WOL target: {Message}", ex.Message);
            }
        }

        return targets;
    }

    private async Task<PowerState> ProbeAsync(CancellationToken ct)
    {
        try
        {
            return await _control.GetPowerStateAsync(ct).ConfigureAwait(false);
        }
        catch (TvException ex) when (ex.Code is TvErrorCode.TvOff)
        {
            return PowerState.Standby;
        }
        catch (TvException ex) when (ex.Code is TvErrorCode.TvUnreachable or TvErrorCode.Timeout)
        {
            return PowerState.Unreachable;
        }
    }

    private static double Elapsed(long started) =>
        Stopwatch.GetElapsedTime(started).TotalSeconds;
}

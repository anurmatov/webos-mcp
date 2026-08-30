using WebosMcp.Application;

namespace WebosMcp.Tests.Fakes;

/// <summary>
/// Fake DIAL endpoint. Lets CI exercise the DIAL-present, DIAL-absent,
/// app-missing, launch-rejected and launched-but-never-foregrounded paths with
/// no physical TV.
/// </summary>
public sealed class FakeDialClient : IDialClient
{
    /// <summary>Null simulates a TV that exposes no DIAL endpoint at all.</summary>
    public Uri? ApplicationUrl { get; set; } = new("http://192.0.2.10:1754/apps/");

    /// <summary>Null simulates the app not being installed (DIAL 404).</summary>
    public DialAppStatus? AppStatus { get; set; } = new("YouTube", "stopped", Installed: true);

    /// <summary>False simulates the TV rejecting the launch POST.</summary>
    public bool LaunchAccepted { get; set; } = true;

    public int ResolveCount { get; private set; }

    public int LaunchCount { get; private set; }

    public List<string> LaunchPayloads { get; } = [];

    public Task<Uri?> ResolveApplicationUrlAsync(CancellationToken cancellationToken)
    {
        ResolveCount++;
        return Task.FromResult(ApplicationUrl);
    }

    public Task<DialAppStatus?> GetAppStatusAsync(
        Uri applicationUrl, string app, CancellationToken cancellationToken) =>
        Task.FromResult(AppStatus);

    public Task<bool> LaunchAppAsync(
        Uri applicationUrl, string app, string payload, CancellationToken cancellationToken)
    {
        LaunchCount++;
        LaunchPayloads.Add(payload);
        return Task.FromResult(LaunchAccepted);
    }
}

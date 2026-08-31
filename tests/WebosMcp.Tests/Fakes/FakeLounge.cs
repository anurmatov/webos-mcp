using System.Runtime.CompilerServices;
using WebosMcp.Application;

namespace WebosMcp.Tests.Fakes;

public sealed record SentCommand(string Command, IReadOnlyDictionary<string, string> Parameters);

/// <summary>
/// A scripted YouTube receiver. Commands are recorded, and the state reports the
/// receiver "announces" are supplied per test — including the case where it
/// announces nothing at all, which must be reported as failure rather than an
/// assumed success.
/// </summary>
public sealed class FakeLoungeSession : ILoungeSession
{
    /// <summary>Reports emitted, in order, when the caller observes.</summary>
    public List<LoungeReceiverState> Reports { get; } = [];

    public List<SentCommand> Sent { get; } = [];

    public bool Disposed { get; private set; }

    /// <summary>Set to have the receiver refuse a command outright.</summary>
    public Exception? SendFailure { get; set; }

    public Task SendAsync(
        string command,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken)
    {
        Sent.Add(new SentCommand(command, parameters ?? new Dictionary<string, string>()));

        return SendFailure is not null ? Task.FromException(SendFailure) : Task.CompletedTask;
    }

    public async IAsyncEnumerable<LoungeReceiverState> ObserveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var report in Reports)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return report;
        }

        // Nothing more will arrive. Block until the caller's budget expires, which is
        // what a real receiver that never confirms looks like.
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeLoungeClient : ILoungeClient
{
    /// <summary>Null simulates a receiver that will not accept remote control.</summary>
    public FakeLoungeSession? Session { get; set; } = new();

    public List<string> ConnectedScreenIds { get; } = [];

    public Task<ILoungeSession?> ConnectAsync(string screenId, CancellationToken cancellationToken)
    {
        ConnectedScreenIds.Add(screenId);
        return Task.FromResult<ILoungeSession?>(Session);
    }
}

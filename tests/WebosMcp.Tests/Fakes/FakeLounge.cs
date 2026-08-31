using System.Runtime.CompilerServices;
using System.Threading.Channels;
using WebosMcp.Application;

namespace WebosMcp.Tests.Fakes;

public sealed record SentCommand(string Command, IReadOnlyDictionary<string, string> Parameters);

/// <summary>
/// A scripted YouTube receiver. Commands are recorded, and the state reports the
/// receiver "announces" are supplied per test — including the case where it
/// announces nothing at all, which must be reported as failure rather than an
/// assumed success.
///
/// It models the ordering the real receiver imposes, which is the point of the fake
/// rather than a detail of it: reports are announced AT THE MOMENT A COMMAND IS
/// SENT, and only to subscriptions that are already open. A caller that sends first
/// and subscribes afterwards therefore observes nothing and fails — the same way the
/// physical receiver behaved when a video that really was playing was reported as
/// never observed. Every observation test in this suite consequently pins the
/// ordering as well as the behaviour it names.
/// </summary>
public sealed class FakeLoungeSession : ILoungeSession
{
    private readonly List<FakeLoungeSubscription> _open = [];

    /// <summary>Reports the receiver announces when a command reaches it.</summary>
    public List<LoungeReceiverState> Reports { get; } = [];

    public List<SentCommand> Sent { get; } = [];

    /// <summary>
    /// Every subscribe and send, in the order they actually happened. This is what an
    /// ordering test asserts against; the two lists above cannot express order
    /// between each other.
    /// </summary>
    public List<string> Interactions { get; } = [];

    public bool Disposed { get; private set; }

    /// <summary>Set to have the receiver refuse a command outright.</summary>
    public Exception? SendFailure { get; set; }

    /// <summary>Set to have the receiver never open its event stream.</summary>
    public Exception? SubscribeFailure { get; set; }

    public Task SendAsync(
        string command,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken)
    {
        Interactions.Add($"send:{command}");
        Sent.Add(new SentCommand(command, parameters ?? new Dictionary<string, string>()));

        if (SendFailure is not null)
        {
            return Task.FromException(SendFailure);
        }

        // Announced only to streams that were already open. A subscription opened
        // after this point has missed them, exactly as on the real receiver.
        foreach (var subscription in _open)
        {
            foreach (var report in Reports)
            {
                subscription.Announce(report);
            }
        }

        return Task.CompletedTask;
    }

    public Task<ILoungeSubscription> SubscribeAsync(CancellationToken cancellationToken)
    {
        Interactions.Add("subscribe");

        if (SubscribeFailure is not null)
        {
            return Task.FromException<ILoungeSubscription>(SubscribeFailure);
        }

        var subscription = new FakeLoungeSubscription();
        _open.Add(subscription);

        return Task.FromResult<ILoungeSubscription>(subscription);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// One open event stream. Reports queued into it are delivered in order; when there
/// are none it blocks until the caller's budget expires, which is what a receiver
/// that never confirms looks like.
/// </summary>
public sealed class FakeLoungeSubscription : ILoungeSubscription
{
    private readonly Channel<LoungeReceiverState> _reports = Channel.CreateUnbounded<LoungeReceiverState>();

    public bool Disposed { get; private set; }

    internal void Announce(LoungeReceiverState state) => _reports.Writer.TryWrite(state);

    public async IAsyncEnumerable<LoungeReceiverState> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var report in _reports.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return report;
        }
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

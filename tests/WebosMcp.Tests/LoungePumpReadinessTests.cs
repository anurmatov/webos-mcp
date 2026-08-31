using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebosMcp.Application;
using WebosMcp.Domain;
using WebosMcp.Infrastructure;
using Xunit;

namespace WebosMcp.Tests;

/// <summary>
/// What "the event stream is ready" actually has to mean.
///
/// An earlier revision took response headers as the barrier: the poll had been
/// accepted, so the stream was called established and the command went out. That is
/// not enough. Headers coming back says the request was received — nobody is reading
/// the body yet. A receiver that announces a state change to whoever is listening at
/// that instant has nobody to announce it to, and the tool reports a video that is
/// visibly playing as never observed. Exactly the fault the ordering fix was for,
/// surviving the ordering fix.
///
/// So these tests run against a transport that returns headers immediately and
/// delivers an event ONLY IF a read is already outstanding, dropping it otherwise.
/// Under that transport a headers-only barrier cannot pass, and only a pump that is
/// genuinely reading can.
/// </summary>
public sealed class LoungePumpReadinessTests
{
    private const string Token = "lounge-token-xyz";
    private const string ScreenId = "screen-abc123";
    private const string Video = "dQw4w9WgXcQ";

    private static string BindResponse()
    {
        var payload = """[[0,["c","SID-1","",8]],[1,["S","GSESSION-1"]]]""";
        return $"{System.Text.Encoding.UTF8.GetByteCount(payload)}\n{payload}";
    }

    /// <summary>One nowPlaying event in the receiver's length-prefixed framing.</summary>
    private static byte[] PlayingChunk(string videoId)
    {
        var payload = $$"""[[1,["nowPlaying",{"videoId":"{{videoId}}","state":"1"}]]]""";
        var bytes = System.Text.Encoding.UTF8.GetByteCount(payload);

        return System.Text.Encoding.UTF8.GetBytes($"{bytes}\n{payload}");
    }

    private static (LoungeClient Client, HandoffStream Poll) Build()
    {
        var poll = new HandoffStream();

        var http = new HandoffPollHandler(
            poll,
            (HttpStatusCode.OK, $$"""{"screens":[{"screenId":"{{ScreenId}}","loungeToken":"{{Token}}"}]}"""),
            (HttpStatusCode.OK, BindResponse()));

        return (new LoungeClient(
            new HttpClient(http),
            Options.Create(new WebosMcpOptions
            {
                LoungeDeviceName = "webos-mcp-test",
                LoungeSubscribeTimeoutSeconds = 5,
            }),
            NullLoggerFactory.Instance,
            NullLogger<LoungeClient>.Instance), poll);
    }

    [Fact]
    public async Task Subscribing_returns_only_once_a_read_is_OUTSTANDING_on_the_stream()
    {
        // The barrier, stated as the property that matters. Headers are already back
        // well before this point; what is asserted is that something is reading.
        var (client, poll) = Build();
        var session = await client.ConnectAsync(ScreenId, CancellationToken.None);

        await using var subscription = await session!.SubscribeAsync(CancellationToken.None);

        Assert.True(
            poll.HasWaitingReader,
            "subscribing must leave a read outstanding on the stream, not merely have received headers");
    }

    [Fact]
    public async Task An_event_announced_right_after_subscribing_is_DELIVERED_not_dropped()
    {
        // The end-to-end shape of the physical fault, against a transport that can
        // actually express it: the receiver announces once, immediately after the
        // command would have gone out. It is delivered only because the pump was
        // already reading. Emit returning false here means it was dropped.
        var (client, poll) = Build();
        var session = await client.ConnectAsync(ScreenId, CancellationToken.None);

        await using var subscription = await session!.SubscribeAsync(CancellationToken.None);

        Assert.True(poll.Emit(PlayingChunk(Video)), "the announcement had no reader to be delivered to");

        var observed = await FirstAsync(subscription, TimeSpan.FromSeconds(5));

        Assert.NotNull(observed);
        Assert.Equal(Video, observed!.VideoId);
        Assert.Equal(LoungePlayerState.Playing, observed.State);
    }

    [Fact]
    public async Task An_event_announced_BEFORE_the_caller_starts_reading_is_held_not_lost()
    {
        // The caller sends its command and only then begins reading. Anything the
        // receiver announced in between must still be there — the pump buffers, so
        // the gap between subscribing and enumerating is not a second race.
        var (client, poll) = Build();
        var session = await client.ConnectAsync(ScreenId, CancellationToken.None);

        await using var subscription = await session!.SubscribeAsync(CancellationToken.None);

        Assert.True(poll.Emit(PlayingChunk(Video)));

        // Deliberately nothing reading at this point, as in the real caller.
        await Task.Delay(50);

        var observed = await FirstAsync(subscription, TimeSpan.FromSeconds(5));

        Assert.Equal(Video, observed!.VideoId);
    }

    [Fact]
    public async Task A_stream_that_ends_before_any_read_lands_fails_WITHOUT_a_command_being_sent()
    {
        // A poll accepted and then immediately closed is not a usable subscription.
        // It has to be reported as such, because the caller's next act is to send a
        // command whose answer nothing would be listening for.
        var http = new HandoffPollHandler(
            new HandoffStream(endImmediately: true),
            (HttpStatusCode.OK, $$"""{"screens":[{"screenId":"{{ScreenId}}","loungeToken":"{{Token}}"}]}"""),
            (HttpStatusCode.OK, BindResponse()));

        var client = new LoungeClient(
            new HttpClient(http),
            Options.Create(new WebosMcpOptions
            {
                LoungeDeviceName = "webos-mcp-test",
                LoungeSubscribeTimeoutSeconds = 5,
            }),
            NullLoggerFactory.Instance,
            NullLogger<LoungeClient>.Instance);

        var session = await client.ConnectAsync(ScreenId, CancellationToken.None);

        var error = await Assert.ThrowsAsync<TvException>(
            () => session!.SubscribeAsync(CancellationToken.None));

        Assert.Contains("No command was sent", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<LoungeReceiverState?> FirstAsync(ILoungeSubscription subscription, TimeSpan within)
    {
        using var budget = new CancellationTokenSource(within);

        try
        {
            await foreach (var state in subscription.ReadAsync(budget.Token))
            {
                return state;
            }
        }
        catch (OperationCanceledException)
        {
            // Nothing arrived in time.
        }

        return null;
    }
}

/// <summary>
/// A stream that hands a payload to a reader that is ALREADY WAITING, and drops it
/// otherwise. Real transports differ in how much they buffer; this one buffers
/// nothing on purpose, so "is anybody reading?" becomes an observable fact rather
/// than an assumption. That is the only way to tell a real readiness barrier from
/// one that just waited for response headers.
/// </summary>
internal sealed class HandoffStream : Stream
{
    private readonly object _gate = new();
    private readonly bool _endImmediately;

    private TaskCompletionSource<byte[]>? _waiting;

    public HandoffStream(bool endImmediately = false) => _endImmediately = endImmediately;

    /// <summary>True while a read is outstanding — i.e. an announcement now would land.</summary>
    public bool HasWaitingReader
    {
        get
        {
            lock (_gate)
            {
                return _waiting is not null;
            }
        }
    }

    /// <summary>
    /// Announces one chunk. Returns false when nothing was reading, which is the
    /// event being lost — the failure this whole class exists to make visible.
    /// </summary>
    public bool Emit(byte[] payload)
    {
        TaskCompletionSource<byte[]>? waiting;

        lock (_gate)
        {
            waiting = _waiting;
            _waiting = null;
        }

        return waiting is not null && waiting.TrySetResult(payload);
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (_endImmediately)
        {
            return 0;
        }

        var pending = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Registered as waiting SYNCHRONOUSLY, before the first await: issuing the
        // read is what makes a reader outstanding, so the production code signalling
        // readiness at that same point must observe this already set.
        lock (_gate)
        {
            _waiting = pending;
        }

        await using var registration = cancellationToken
            .Register(() => pending.TrySetCanceled(cancellationToken))
            .ConfigureAwait(false);

        var payload = await pending.Task.ConfigureAwait(false);

        payload.CopyTo(buffer.Span);
        return payload.Length;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Serves the scripted token and bind responses, then hands every later request the
/// handoff stream as its body — headers immediately, content only to a live reader.
/// </summary>
internal sealed class HandoffPollHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _scripted;
    private readonly Stream _poll;

    public HandoffPollHandler(Stream poll, params (HttpStatusCode Status, string Body)[] scripted)
    {
        _poll = poll;
        _scripted = new Queue<(HttpStatusCode, string)>(scripted);
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (_scripted.Count > 0)
        {
            var (status, body) = _scripted.Dequeue();

            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(_poll),
        });
    }
}

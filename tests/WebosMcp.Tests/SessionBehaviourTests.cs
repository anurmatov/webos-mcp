using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using WebosMcp.Domain;
using WebosMcp.Tests.Fakes;
using Xunit;

namespace WebosMcp.Tests;

/// <summary>Concurrency serialization, reconnect after sleep/reboot, timeout and cancellation.</summary>
public sealed class SessionBehaviourTests
{
    [Fact]
    public async Task Concurrent_button_sequences_are_serialized_not_interleaved()
    {
        var connection = new FakeSsapConnection();
        var order = new List<string>();
        var orderLock = new object();

        // Yield inside every wire operation so an unserialized implementation
        // would demonstrably interleave here.
        connection.OnRequestEnter = async target =>
        {
            lock (orderLock)
            {
                order.Add(target);
            }

            await Task.Yield();
            await Task.Delay(1);
        };

        var harness = new TestHarness(connection);

        var first = harness.Control.SendButtonAsync(RemoteButton.Up, 5, CancellationToken.None);
        var second = harness.Control.SendButtonAsync(RemoteButton.Down, 5, CancellationToken.None);
        await Task.WhenAll(first, second);

        Assert.Equal(10, order.Count);

        // Each caller's five presses must form one contiguous run.
        var boundaries = 0;
        for (var i = 1; i < order.Count; i++)
        {
            if (order[i] != order[i - 1])
            {
                boundaries++;
            }
        }

        Assert.Equal(1, boundaries);
    }

    [Fact]
    public async Task Concurrent_text_entry_does_not_interleave_with_a_button_sequence()
    {
        var connection = new FakeSsapConnection();
        var active = 0;
        var maxConcurrent = 0;
        var gate = new object();

        connection.OnRequestEnter = async _ =>
        {
            lock (gate)
            {
                active++;
                maxConcurrent = Math.Max(maxConcurrent, active);
            }

            await Task.Delay(1);
        };

        connection.OnRequestExit = _ =>
        {
            lock (gate)
            {
                active--;
            }

            return Task.CompletedTask;
        };

        var harness = new TestHarness(connection);

        await Task.WhenAll(
            harness.Control.TypeTextAsync("hello", false, true, CancellationToken.None),
            harness.Control.SendButtonAsync(RemoteButton.Left, 4, CancellationToken.None),
            harness.Control.TypeTextAsync("world", false, true, CancellationToken.None));

        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task A_dropped_socket_reconnects_transparently_without_a_process_restart()
    {
        var dropped = new FakeSsapConnection();
        dropped.TransientFailures["ssap://audio/setVolume"] =
            new Queue<Exception>([new WebSocketException("the TV went to sleep")]);

        var fresh = new FakeSsapConnection();

        var harness = new TestHarness(dropped);
        harness.Factory.Enqueue(fresh);

        await harness.Control.SetVolumeAsync(20, CancellationToken.None);

        // Two connections created: the dropped one and its replacement.
        Assert.Equal(2, harness.Factory.CreateCount);
        Assert.True(dropped.Disposed);
        Assert.Contains("ssap://audio/setVolume", fresh.RequestUris);
    }

    [Fact]
    public async Task A_reconnect_re_registers_with_the_stored_client_key()
    {
        var dropped = new FakeSsapConnection();
        dropped.TransientFailures["ssap://system/turnOff"] =
            new Queue<Exception>([new WebSocketException("reset by peer")]);

        var fresh = new FakeSsapConnection();

        var harness = new TestHarness(dropped);
        harness.Factory.Enqueue(fresh);

        await harness.Control.PowerOffAsync(CancellationToken.None);

        Assert.Equal(1, fresh.RegisterCount);
        Assert.Equal("test-client-key", fresh.LastClientKey);
    }

    [Fact]
    public async Task A_reissued_client_key_is_persisted()
    {
        var connection = new FakeSsapConnection { IssuedClientKey = "rotated-key" };
        var harness = new TestHarness(connection);

        await harness.Control.PowerOffAsync(CancellationToken.None);

        Assert.Contains("rotated-key", harness.KeyStore.Writes);
    }

    [Fact]
    public async Task The_connection_is_reused_across_calls()
    {
        var harness = new TestHarness();

        await harness.Control.PowerOffAsync(CancellationToken.None);
        await harness.Control.ScreenOnAsync(CancellationToken.None);
        await harness.Control.SetMuteAsync(false, CancellationToken.None);

        Assert.Equal(1, harness.Factory.CreateCount);
        Assert.Equal(1, harness.Connection.ConnectCount);
    }

    [Fact]
    public async Task A_stalled_request_times_out_rather_than_hanging_forever()
    {
        var connection = new FakeSsapConnection { HangForever = true };
        var harness = new TestHarness(connection, options => options.RequestTimeoutSeconds = 1);

        var start = DateTimeOffset.UtcNow;
        var ex = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.GetPowerStateAsync(CancellationToken.None));
        var elapsed = DateTimeOffset.UtcNow - start;

        Assert.Equal(TvErrorCode.Timeout, ex.Code);

        // Bounded, not merely "eventually throws".
        Assert.True(elapsed < TimeSpan.FromSeconds(15), $"took {elapsed}");
    }

    [Fact]
    public async Task A_timed_out_request_drops_the_connection_so_the_next_call_reconnects()
    {
        var stalled = new FakeSsapConnection { HangForever = true };
        var fresh = new FakeSsapConnection();

        var harness = new TestHarness(stalled, options => options.RequestTimeoutSeconds = 1);
        harness.Factory.Enqueue(fresh);

        await Assert.ThrowsAsync<TvException>(
            () => harness.Control.GetPowerStateAsync(CancellationToken.None));

        Assert.True(stalled.Disposed);

        await harness.Control.ScreenOnAsync(CancellationToken.None);
        Assert.Equal(2, harness.Factory.CreateCount);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_is_not_reported_as_a_timeout()
    {
        var connection = new FakeSsapConnection { HangForever = true };
        var harness = new TestHarness(connection, options => options.RequestTimeoutSeconds = 30);

        using var cts = new CancellationTokenSource();
        var pending = harness.Control.GetPowerStateAsync(cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Reset_forces_the_next_call_onto_a_fresh_connection()
    {
        var harness = new TestHarness();

        await harness.Control.PowerOffAsync(CancellationToken.None);
        await harness.Session.ResetAsync();
        await harness.Control.PowerOffAsync(CancellationToken.None);

        Assert.Equal(2, harness.Factory.CreateCount);
    }

    [Fact]
    public async Task The_client_key_never_appears_in_logs_or_exception_text()
    {
        const string secret = "super-secret-client-key-value";

        var capture = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(capture);
        });

        var connection = new FakeSsapConnection();
        connection.TransientFailures["ssap://audio/setVolume"] =
            new Queue<Exception>([new WebSocketException("dropped")]);

        var harness = new TestHarness(connection, loggerFactory: loggerFactory);
        harness.KeyStore.Current = secret;

        var second = new FakeSsapConnection();
        harness.Factory.Enqueue(second);

        await harness.Control.SetVolumeAsync(10, CancellationToken.None);

        // Also exercise a failing path, which is where secrets usually leak.
        var failing = new FakeSsapConnection { RegisterFailure = TvException.PairingRequired() };
        harness.Factory.Enqueue(failing);
        await harness.Session.ResetAsync();

        var ex = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.ScreenOnAsync(CancellationToken.None));

        Assert.DoesNotContain(secret, ex.ToString(), StringComparison.Ordinal);
        Assert.All(capture.Lines, line => Assert.DoesNotContain(secret, line, StringComparison.Ordinal));
        Assert.NotEmpty(capture.Lines);
    }
}

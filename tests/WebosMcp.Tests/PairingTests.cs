using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebosMcp.Application;
using WebosMcp.Domain;
using WebosMcp.Infrastructure;
using WebosMcp.Tests.Fakes;
using Xunit;

namespace WebosMcp.Tests;

/// <summary>
/// The opt-in pairing surface. Every distinct outcome — denial, timeout,
/// read-only storage, already-paired — gets its own test, because the whole
/// point of the contract is that a caller can tell them apart.
/// </summary>
public sealed class PairingTests
{
    private const string Secret = "super-secret-issued-client-key";

    private static (PairingService Service, FakeClientKeyStore Store, FakeSsapConnectionFactory Factory)
        Build(FakeSsapConnection connection, Action<WebosMcpOptions>? configure = null,
              string? existingKey = null, ILoggerFactory? loggerFactory = null)
    {
        var options = new WebosMcpOptions
        {
            Host = "192.0.2.10",
            PairingTimeoutSeconds = 1,
            ClientKeyPath = "/test/clientkey.json",
        };
        configure?.Invoke(options);

        var factory = new FakeSsapConnectionFactory().Enqueue(connection);
        var store = new FakeClientKeyStore(existingKey);
        var logs = loggerFactory ?? NullLoggerFactory.Instance;

        var service = new PairingService(
            factory, store, Options.Create(options), logs.CreateLogger<PairingService>());

        return (service, store, factory);
    }

    // ------------------------------------------------------------- happy path

    [Fact]
    public async Task Pairing_persists_the_key_and_reports_verified_on_disk()
    {
        var connection = new FakeSsapConnection { IssuedClientKey = Secret };
        var (service, store, _) = Build(connection);

        var outcome = await service.PairAsync(force: false, CancellationToken.None);

        Assert.False(outcome.AlreadyPaired);
        Assert.True(outcome.VerifiedOnDisk);
        Assert.Equal("/test/clientkey.json", outcome.Location);

        // Went through the durable persist path, not the best-effort cache write.
        Assert.Equal([Secret], store.Persists);
    }

    [Fact]
    public async Task Pairing_registers_without_a_key_so_a_stale_one_cannot_confuse_the_result()
    {
        var connection = new FakeSsapConnection { IssuedClientKey = Secret };
        var (service, _, _) = Build(connection, existingKey: "stale-key");

        await service.PairAsync(force: true, CancellationToken.None);

        // A fresh prompt is requested with no key attached, so an SSAP error can
        // only mean "declined", never "stale key refused".
        Assert.Null(connection.LastClientKey);
    }

    // ------------------------------------------------------------ already paired

    [Fact]
    public async Task An_accepted_existing_key_short_circuits_without_prompting_anyone()
    {
        var probe = new FakeSsapConnection();
        var (service, store, _) = Build(probe, existingKey: "working-key");

        var outcome = await service.PairAsync(force: false, CancellationToken.None);

        Assert.True(outcome.AlreadyPaired);
        Assert.True(outcome.VerifiedOnDisk);

        // Nothing was re-persisted and nobody was sent to the TV.
        Assert.Empty(store.Persists);
        Assert.Equal("working-key", probe.LastClientKey);
    }

    [Fact]
    public async Task Force_re_pairs_even_when_a_working_key_is_stored()
    {
        var connection = new FakeSsapConnection { IssuedClientKey = Secret };
        var (service, store, _) = Build(connection, existingKey: "working-key");

        var outcome = await service.PairAsync(force: true, CancellationToken.None);

        Assert.False(outcome.AlreadyPaired);
        Assert.Equal([Secret], store.Persists);
    }

    [Fact]
    public async Task A_refused_stored_key_falls_through_to_a_real_pairing()
    {
        var refusing = new FakeSsapConnection { RegisterFailure = TvException.PairingRequired() };
        var pairing = new FakeSsapConnection { IssuedClientKey = Secret };

        var (service, store, factory) = Build(refusing, existingKey: "stale-key");
        factory.Enqueue(pairing);

        var outcome = await service.PairAsync(force: false, CancellationToken.None);

        Assert.False(outcome.AlreadyPaired);
        Assert.Equal([Secret], store.Persists);
    }

    // ------------------------------------------------------------------ denial

    [Fact]
    public async Task A_declined_prompt_returns_PAIRING_DENIED()
    {
        var connection = new FakeSsapConnection
        {
            RegisterFailure = TvException.PairingDenied(),
        };
        var (service, store, _) = Build(connection);

        var ex = await Assert.ThrowsAsync<TvException>(
            () => service.PairAsync(force: false, CancellationToken.None));

        Assert.Equal(TvErrorCode.PairingDenied, ex.Code);
        Assert.Equal("PAIRING_DENIED", ex.Code.ToWireCode());
        Assert.Empty(store.Persists);
    }

    [Fact]
    public void A_register_error_with_no_key_supplied_is_a_decline_not_a_stale_key()
    {
        // No key was sent, so there was nothing to reject — somebody said no.
        Assert.True(SsapWebSocketConnection.IsExplicitDecline("user denied the request"));
        Assert.True(SsapWebSocketConnection.IsExplicitDecline("pairing was rejected"));
        Assert.True(SsapWebSocketConnection.IsExplicitDecline("cancelled by user"));

        // Plain "access denied" is the ordinary wording for a refused stale key
        // and must NOT be read as somebody actively declining.
        Assert.False(SsapWebSocketConnection.IsExplicitDecline("403 access denied"));
    }

    // ----------------------------------------------------------------- timeout

    [Fact]
    public async Task An_unanswered_prompt_returns_PAIRING_TIMEOUT_not_a_generic_timeout()
    {
        var connection = new FakeSsapConnection { HangOnRegister = true };
        var (service, store, _) = Build(connection, o => o.PairingTimeoutSeconds = 1);

        var start = DateTimeOffset.UtcNow;
        var ex = await Assert.ThrowsAsync<TvException>(
            () => service.PairAsync(force: false, CancellationToken.None));
        var elapsed = DateTimeOffset.UtcNow - start;

        Assert.Equal(TvErrorCode.PairingTimeout, ex.Code);
        Assert.Equal("PAIRING_TIMEOUT", ex.Code.ToWireCode());
        Assert.NotEqual(TvErrorCode.Timeout, ex.Code);

        // Bounded, not merely "eventually throws".
        Assert.True(elapsed < TimeSpan.FromSeconds(15), $"took {elapsed}");
        Assert.Empty(store.Persists);
    }

    [Fact]
    public async Task Caller_cancellation_during_pairing_is_not_reported_as_a_pairing_timeout()
    {
        var connection = new FakeSsapConnection { HangOnRegister = true };
        var (service, _, _) = Build(connection, o => o.PairingTimeoutSeconds = 120);

        using var cts = new CancellationTokenSource();
        var pending = service.PairAsync(force: false, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    // -------------------------------------------------------- read-only storage

    [Fact]
    public async Task Read_only_storage_fails_BEFORE_anyone_is_sent_to_the_tv()
    {
        var connection = new FakeSsapConnection { IssuedClientKey = Secret };
        var (service, store, _) = Build(connection);
        store.DurableWritablePath = null;   // e.g. only a read-only mounted secret

        var ex = await Assert.ThrowsAsync<TvException>(
            () => service.PairAsync(force: false, CancellationToken.None));

        Assert.Equal(TvErrorCode.KeyStorageReadOnly, ex.Code);
        Assert.Equal("KEY_STORAGE_READONLY", ex.Code.ToWireCode());

        // The whole point: no prompt was raised, so no physical trip was wasted
        // on a key that could not have been kept.
        Assert.Equal(0, connection.RegisterCount);
        Assert.Empty(store.Persists);
    }

    // ------------------------------------------------------- secret containment

    [Fact]
    public async Task The_client_key_never_appears_in_the_outcome_or_in_logs()
    {
        var capture = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(capture);
        });

        var connection = new FakeSsapConnection { IssuedClientKey = Secret };
        var (service, _, _) = Build(connection, loggerFactory: loggerFactory);

        var outcome = await service.PairAsync(force: false, CancellationToken.None);

        var serialised = JsonSerializer.Serialize(outcome);
        Assert.DoesNotContain(Secret, serialised, StringComparison.Ordinal);

        Assert.NotEmpty(capture.Lines);
        Assert.All(capture.Lines, line => Assert.DoesNotContain(Secret, line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Error_messages_never_carry_the_client_key()
    {
        var connection = new FakeSsapConnection { IssuedClientKey = Secret };
        var (service, store, _) = Build(connection);
        store.DurableWritablePath = null;

        var ex = await Assert.ThrowsAsync<TvException>(
            () => service.PairAsync(force: false, CancellationToken.None));

        Assert.DoesNotContain(Secret, ex.ToString(), StringComparison.Ordinal);
    }

    // -------------------------------------------------- end to end over MCP

    [Fact]
    public async Task Pair_device_over_mcp_reports_the_location_and_never_the_key()
    {
        var connection = new FakeSsapConnection { IssuedClientKey = Secret };
        var keyStore = new FakeClientKeyStore(null) { DurableWritablePath = "/durable/clientkey.json" };

        var capture = new CapturingLoggerProvider();
        await using var fixture = await StdioFixture.StartAsync(
            connection, enablePairing: true, keyStore: keyStore, loggerProvider: capture);

        var tools = await fixture.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        Assert.Contains("pair_device", tools.Select(t => t.Name));

        var result = await fixture.Client.CallToolAsync(
            "pair_device", cancellationToken: CancellationToken.None);

        var raw = string.Concat(result.Content
            .OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text));

        using var payload = JsonDocument.Parse(raw);
        Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());

        var body = payload.RootElement.GetProperty("result");
        Assert.Equal("paired", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("verifiedOnDisk").GetBoolean());
        Assert.Equal("/durable/clientkey.json", body.GetProperty("location").GetString());

        // The key must not cross the MCP boundary in any form.
        Assert.DoesNotContain(Secret, raw, StringComparison.Ordinal);
        Assert.All(capture.Lines, l => Assert.DoesNotContain(Secret, l, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pair_device_over_mcp_surfaces_a_denial_as_a_typed_error()
    {
        var connection = new FakeSsapConnection { RegisterFailure = TvException.PairingDenied() };

        await using var fixture = await StdioFixture.StartAsync(
            connection, enablePairing: true,
            keyStore: new FakeClientKeyStore(null) { DurableWritablePath = "/durable/clientkey.json" });

        var result = await fixture.Client.CallToolAsync(
            "pair_device", cancellationToken: CancellationToken.None);

        using var payload = JsonDocument.Parse(string.Concat(result.Content
            .OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text)));

        Assert.False(payload.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "PAIRING_DENIED",
            payload.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}

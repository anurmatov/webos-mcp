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
/// The real on-disk key store. The fake used elsewhere cannot prove durability,
/// and durability is the entire point of the pairing persistence contract.
/// </summary>
public sealed class ClientKeyStoreTests : IDisposable
{
    private const string Secret = "persisted-client-key-value";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "webos-mcp-tests-" + Guid.NewGuid().ToString("N"));

    private FileClientKeyStore Build(Action<WebosMcpOptions>? configure = null, ILoggerFactory? logs = null)
    {
        Directory.CreateDirectory(_root);

        var options = new WebosMcpOptions
        {
            Host = "192.0.2.10",
            ClientKeyPath = Path.Combine(_root, "clientkey.json"),
        };
        configure?.Invoke(options);

        return new FileClientKeyStore(
            Options.Create(options),
            (logs ?? NullLoggerFactory.Instance).CreateLogger<FileClientKeyStore>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Persist_writes_the_key_and_reads_it_back_from_a_fresh_store()
    {
        var store = Build();

        var location = await store.PersistAsync(Secret, CancellationToken.None);

        Assert.Equal(Path.Combine(_root, "clientkey.json"), location);
        Assert.True(File.Exists(location));

        // A FRESH store proves it survived the process, not just the cache.
        var reread = await Build().ReadAsync(CancellationToken.None);
        Assert.Equal(Secret, reread);
    }

    [Fact]
    public async Task Persist_leaves_no_temp_file_behind()
    {
        var store = Build();
        await store.PersistAsync(Secret, CancellationToken.None);

        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task Persist_overwrites_an_existing_key_atomically()
    {
        var store = Build();
        await store.PersistAsync("first-key", CancellationToken.None);
        await store.PersistAsync(Secret, CancellationToken.None);

        Assert.Equal(Secret, await Build().ReadAsync(CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task Persist_writes_the_json_shape_the_read_path_accepts()
    {
        var store = Build();
        var location = await store.PersistAsync(Secret, CancellationToken.None);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(location));
        Assert.Equal(Secret, document.RootElement.GetProperty("clientKey").GetString());
    }

    [Fact]
    public async Task Persist_refuses_an_empty_key()
    {
        var store = Build();

        var ex = await Assert.ThrowsAsync<TvException>(
            () => store.PersistAsync("   ", CancellationToken.None));

        Assert.Equal(TvErrorCode.InvalidInput, ex.Code);
    }

    // ------------------------------------------------------- read-only sources

    [Fact]
    public void A_mounted_secret_with_no_writable_path_has_no_durable_destination()
    {
        var store = Build(o =>
        {
            o.ClientKeyPath = null;
            o.ClientKeyFile = "/run/secrets/webos_client_key";
        });

        Assert.Null(store.DurableWritablePath);
    }

    [Fact]
    public async Task Persist_returns_KEY_STORAGE_READONLY_when_there_is_no_writable_destination()
    {
        var store = Build(o =>
        {
            o.ClientKeyPath = null;
            o.ClientKeyFile = "/run/secrets/webos_client_key";
        });

        var ex = await Assert.ThrowsAsync<TvException>(
            () => store.PersistAsync(Secret, CancellationToken.None));

        Assert.Equal(TvErrorCode.KeyStorageReadOnly, ex.Code);
        Assert.Equal("KEY_STORAGE_READONLY", ex.Code.ToWireCode());
    }

    [Fact]
    public async Task Persist_returns_KEY_STORAGE_READONLY_when_the_destination_cannot_be_created()
    {
        Directory.CreateDirectory(_root);

        // Put a FILE where the key's parent directory would have to be. The
        // write then fails for everyone — this is deliberately not a
        // permission-bit test, because mode bits do not restrain root and the
        // check would silently no-op in a root CI container.
        var blocker = Path.Combine(_root, "blocker");
        await File.WriteAllTextAsync(blocker, "not a directory");

        var store = Build(o => o.ClientKeyPath = Path.Combine(blocker, "clientkey.json"));

        var ex = await Assert.ThrowsAsync<TvException>(
            () => store.PersistAsync(Secret, CancellationToken.None));

        Assert.Equal(TvErrorCode.KeyStorageReadOnly, ex.Code);
        Assert.Equal("KEY_STORAGE_READONLY", ex.Code.ToWireCode());
    }

    [Fact]
    public async Task An_explicit_writable_path_wins_over_a_read_only_mounted_secret()
    {
        var writable = Path.Combine(_root, "durable.json");
        var store = Build(o =>
        {
            o.ClientKeyPath = writable;
            o.ClientKeyFile = "/run/secrets/webos_client_key";
        });

        Assert.Equal(writable, store.DurableWritablePath);
        Assert.Equal(writable, await store.PersistAsync(Secret, CancellationToken.None));
    }

    // ------------------------------------------------------- secret containment

    [Fact]
    public async Task Persisting_never_logs_the_key()
    {
        var capture = new CapturingLoggerProvider();
        using var logs = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(capture);
        });

        var store = Build(logs: logs);
        await store.PersistAsync(Secret, CancellationToken.None);

        Assert.NotEmpty(capture.Lines);
        Assert.All(capture.Lines, l => Assert.DoesNotContain(Secret, l, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cache_only_reissue_warns_instead_of_short_circuiting_silently()
    {
        var capture = new CapturingLoggerProvider();
        using var logs = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(capture);
        });

        var store = Build(o =>
        {
            o.ClientKeyPath = null;
            o.ClientKeyFile = "/run/secrets/webos_client_key";
        }, logs);

        await store.WriteAsync(Secret, CancellationToken.None);

        // The operator must be able to see that a reissued key will not survive
        // a restart; silence here is what made the old behaviour dangerous.
        Assert.Contains(capture.Lines, l =>
            l.Contains("memory only", StringComparison.OrdinalIgnoreCase));
        Assert.All(capture.Lines, l => Assert.DoesNotContain(Secret, l, StringComparison.Ordinal));
    }
}

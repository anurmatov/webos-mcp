using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebosMcp.Application;
using WebosMcp.Domain;

namespace WebosMcp.Infrastructure;

/// <summary>
/// Resolves the pairing client key from, in order: an inline environment
/// value, a mounted secret file, then the user-owned local key file that
/// <c>pair</c> writes. The key is never logged and never returned by a tool.
/// </summary>
public sealed class FileClientKeyStore : IClientKeyStore
{
    private readonly WebosMcpOptions _options;
    private readonly ILogger<FileClientKeyStore> _logger;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    private string? _cached;

    public FileClientKeyStore(IOptions<WebosMcpOptions> options, ILogger<FileClientKeyStore> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_cached))
        {
            return _cached;
        }

        if (!string.IsNullOrWhiteSpace(_options.ClientKey))
        {
            _cached = _options.ClientKey!.Trim();
            return _cached;
        }

        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(_options.ClientKeyFile) && File.Exists(_options.ClientKeyFile))
            {
                var contents = (await File.ReadAllTextAsync(_options.ClientKeyFile, cancellationToken)
                    .ConfigureAwait(false)).Trim();

                if (!string.IsNullOrWhiteSpace(contents))
                {
                    _cached = TryExtractFromJson(contents) ?? contents;
                    return _cached;
                }
            }

            var path = _options.ResolvedClientKeyPath;
            if (File.Exists(path))
            {
                var contents = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                _cached = TryExtractFromJson(contents);
                return _cached;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately does not echo the file body — it holds the key.
            _logger.LogWarning("Could not read the stored client key: {Message}", ex.Message);
        }
        finally
        {
            _ioLock.Release();
        }

        return _cached;
    }

    public async Task WriteAsync(string clientKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientKey))
        {
            throw TvException.Invalid("Refusing to persist an empty client key.");
        }

        // An inline key or a mounted secret is operator-owned and read-only to
        // us, so a mid-session reissue can only be cached. Say so: a silent
        // cache-only update is indistinguishable from a durable one, and the
        // difference only surfaces after a restart.
        if (!string.IsNullOrWhiteSpace(_options.ClientKey) || !string.IsNullOrWhiteSpace(_options.ClientKeyFile))
        {
            _cached = clientKey.Trim();
            _logger.LogWarning(
                "The TV reissued a client key, but the configured key source ({Location}) is read-only to this " +
                "process, so the new key is held in memory only and will be lost on restart. Configure " +
                "WEBOSMCP__CLIENTKEYPATH with a durable writable location to persist reissued keys.",
                DescribeLocation());
            return;
        }

        var path = _options.ResolvedClientKeyPath;
        var directory = Path.GetDirectoryName(path);

        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(new StoredKey(clientKey.Trim()));
            await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
            TryRestrictPermissions(path);

            _cached = clientKey.Trim();
            _logger.LogInformation("Client key stored at {Path}.", path);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// The durable writable location. An explicitly configured
    /// <c>ClientKeyPath</c> always wins; otherwise the default user-owned path
    /// is used, but only when no read-only source is configured — writing to
    /// the default while reading from a mounted secret would silently diverge.
    /// </summary>
    public string? DurableWritablePath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_options.ClientKeyPath))
            {
                return _options.ClientKeyPath;
            }

            if (!string.IsNullOrWhiteSpace(_options.ClientKey) || !string.IsNullOrWhiteSpace(_options.ClientKeyFile))
            {
                return null;
            }

            return _options.ResolvedClientKeyPath;
        }
    }

    public async Task<string> PersistAsync(string clientKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientKey))
        {
            throw TvException.Invalid("Refusing to persist an empty client key.");
        }

        var path = DurableWritablePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw TvException.KeyStorageReadOnly(
                $"No durable writable key location is configured. The current key source ({DescribeLocation()}) " +
                "is read-only to this process. Set WEBOSMCP__CLIENTKEYPATH to a writable path — in a container, " +
                "a mounted volume the process owns — and retry.");
        }

        var trimmed = clientKey.Trim();
        var directory = Path.GetDirectoryName(path);

        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(new StoredKey(trimmed));

                // Write to a sibling temp file, flush it to the device, then
                // rename over the target. A crash mid-write therefore leaves
                // either the old key or the new one, never a truncated file.
                var temp = path + ".tmp";
                await using (var stream = new FileStream(
                    temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                TryRestrictPermissions(temp);
                File.Move(temp, path, overwrite: true);
                TryRestrictPermissions(path);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                throw TvException.KeyStorageReadOnly(
                    $"The client key could not be written to '{path}': {ex.Message}. " +
                    "The location must exist and be writable by this process.");
            }

            // Verify by RE-READING FROM DISK. Reporting success from the value
            // we just held in memory would report success for a write that
            // never landed — exactly the failure this whole path exists to
            // prevent.
            string verified;
            try
            {
                var contents = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                verified = TryExtractFromJson(contents) ?? contents.Trim();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                throw TvException.KeyStorageReadOnly(
                    $"The client key was written to '{path}' but could not be read back for verification: {ex.Message}.");
            }

            if (!string.Equals(verified, trimmed, StringComparison.Ordinal))
            {
                throw TvException.KeyStorageReadOnly(
                    $"The client key written to '{path}' did not read back identically. Storage may be full, " +
                    "read-only, or shadowed by another mount.");
            }

            _cached = trimmed;

            // Location only, never the key.
            _logger.LogInformation("Client key persisted and verified at {Path}.", path);
            return path;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public string DescribeLocation()
    {
        if (!string.IsNullOrWhiteSpace(_options.ClientKey))
        {
            return "environment variable WEBOSMCP__CLIENTKEY";
        }

        if (!string.IsNullOrWhiteSpace(_options.ClientKeyFile))
        {
            return $"mounted secret file {_options.ClientKeyFile}";
        }

        return _options.ResolvedClientKeyPath;
    }

    private static void TryRestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best effort — the operator owns the filesystem policy.
        }
    }

    private static string? TryExtractFromJson(string contents)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<StoredKey>(contents);
            return string.IsNullOrWhiteSpace(stored?.ClientKey) ? null : stored!.ClientKey;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record StoredKey([property: JsonPropertyName("clientKey")] string ClientKey);
}

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

        // An inline key or a mounted secret is operator-owned and read-only to us.
        if (!string.IsNullOrWhiteSpace(_options.ClientKey) || !string.IsNullOrWhiteSpace(_options.ClientKeyFile))
        {
            _cached = clientKey.Trim();
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

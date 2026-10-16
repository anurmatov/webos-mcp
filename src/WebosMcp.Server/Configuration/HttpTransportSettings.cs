using System.Net;

namespace WebosMcp.Server.Configuration;

/// <summary>
/// HTTP transport bind and auth policy.
///
/// The HTTP transport can perform state-changing actions on the TV over the
/// network, so it does not inherit stdio's "trust the local process"
/// assumption. Default bind is loopback-only; any non-loopback bind REQUIRES a
/// token, and the server refuses to start otherwise rather than silently
/// serving unauthenticated TV control to the network.
///
/// Alternatively the transport can listen only on a unix socket
/// (<see cref="UnixSocketVariable"/>), opening no TCP port at all. Access is then
/// governed by the socket's file mode (0600 or 0660) and its directory.
/// </summary>
public sealed class HttpTransportSettings
{
    public const string DefaultBindAddress = "127.0.0.1";
    public const int DefaultPort = 8765;

    public const string TokenVariable = "WEBOS_MCP_HTTP_TOKEN";
    public const string TokenFileVariable = "WEBOS_MCP_HTTP_TOKEN_FILE";
    public const string BindVariable = "WEBOS_MCP_HTTP_BIND";
    public const string PortVariable = "WEBOS_MCP_HTTP_PORT";
    public const string UnixSocketVariable = "WEBOS_MCP_HTTP_UNIX_SOCKET";
    public const string UnixSocketModeVariable = "WEBOS_MCP_HTTP_UNIX_SOCKET_MODE";

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerAndGroup = OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;

    public required string BindAddress { get; init; }

    public required int Port { get; init; }

    /// <summary>Null when no token is configured (only legal on a loopback bind).</summary>
    public string? Token { get; init; }

    /// <summary>When set, the transport listens only on this unix socket and opens no TCP port.</summary>
    public string? UnixSocketPath { get; init; }

    /// <summary>The file mode applied to <see cref="UnixSocketPath"/> before any request is served.</summary>
    public UnixFileMode UnixSocketMode { get; init; } = OwnerOnly;

    public bool RequiresAuth => !string.IsNullOrEmpty(Token);

    public string Url => UnixSocketPath is null ? $"http://{BindAddress}:{Port}" : $"http://unix:{UnixSocketPath}";

    /// <summary>
    /// Resolves settings from the environment and enforces the bind/token
    /// invariant. Throws <see cref="HttpTransportConfigurationException"/> when
    /// the combination is unsafe.
    /// </summary>
    public static HttpTransportSettings Resolve(IReadOnlyDictionary<string, string?> environment)
    {
        var socketPath = Value(environment, UnixSocketVariable);
        var modeText = Value(environment, UnixSocketModeVariable);

        if (socketPath is not null)
        {
            return ResolveUnixSocket(environment, socketPath, modeText);
        }

        if (modeText is not null)
        {
            throw new HttpTransportConfigurationException(
                $"{UnixSocketModeVariable} is set but {UnixSocketVariable} is not. Set both, or neither.");
        }

        var bind = Value(environment, BindVariable) ?? DefaultBindAddress;
        var portText = Value(environment, PortVariable);

        var port = DefaultPort;
        if (portText is not null)
        {
            if (!int.TryParse(portText, out port) || port is < 1 or > 65535)
            {
                throw new HttpTransportConfigurationException(
                    $"{PortVariable} must be a TCP port between 1 and 65535; got '{portText}'.");
            }
        }

        var token = ResolveToken(environment);

        if (!IsLoopback(bind) && string.IsNullOrEmpty(token))
        {
            throw new HttpTransportConfigurationException(
                $"Refusing to start: the HTTP transport is configured to bind to '{bind}', which is not loopback, " +
                $"but no auth token is configured. Set {TokenVariable} or {TokenFileVariable}, or bind to " +
                $"{DefaultBindAddress}. Serving unauthenticated state-changing TV control to the network is not permitted.");
        }

        return new HttpTransportSettings
        {
            BindAddress = bind,
            Port = port,
            Token = token,
        };
    }

    private static HttpTransportSettings ResolveUnixSocket(
        IReadOnlyDictionary<string, string?> environment,
        string socketPath,
        string? modeText)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new HttpTransportConfigurationException(
                $"{UnixSocketVariable} is supported on Linux and macOS only.");
        }

        if (!Path.IsPathFullyQualified(socketPath))
        {
            throw new HttpTransportConfigurationException(
                $"{UnixSocketVariable} must be an absolute path; got '{socketPath}'.");
        }

        // A socket together with a TCP bind or port is ambiguous intent: fail loudly
        // rather than guess which listener the operator meant.
        if (Value(environment, BindVariable) is not null || Value(environment, PortVariable) is not null)
        {
            throw new HttpTransportConfigurationException(
                $"Refusing to start: {UnixSocketVariable} is set together with {BindVariable} or {PortVariable}. " +
                $"The unix socket replaces the TCP listener; unset {BindVariable} and {PortVariable}.");
        }

        var mode = modeText switch
        {
            null or "0600" => OwnerOnly,
            "0660" => OwnerAndGroup,
            _ => throw new HttpTransportConfigurationException(
                $"{UnixSocketModeVariable} must be 0600 or 0660; got '{modeText}'."),
        };

        return new HttpTransportSettings
        {
            BindAddress = DefaultBindAddress,
            Port = DefaultPort,
            Token = ResolveToken(environment),
            UnixSocketPath = socketPath,
            UnixSocketMode = mode,
        };
    }

    private static string? ResolveToken(IReadOnlyDictionary<string, string?> environment)
    {
        // A mounted secret file wins over an inline variable: it is the
        // recommended container path and is easier to rotate.
        var path = Value(environment, TokenFileVariable);
        if (path is not null)
        {
            if (!File.Exists(path))
            {
                throw new HttpTransportConfigurationException(
                    $"{TokenFileVariable} points at '{path}', which does not exist.");
            }

            var contents = File.ReadAllText(path).Trim();
            if (contents.Length == 0)
            {
                throw new HttpTransportConfigurationException(
                    $"{TokenFileVariable} points at '{path}', which is empty.");
            }

            return contents;
        }

        return Value(environment, TokenVariable);
    }

    /// <summary>
    /// Treats the whole 127.0.0.0/8 range and ::1 as loopback. A wildcard bind
    /// ("0.0.0.0", "*", "[::]") is explicitly NOT loopback.
    /// </summary>
    internal static bool IsLoopback(string bind)
    {
        var value = bind.Trim();
        if (value.Length == 0)
        {
            return false;
        }

        if (value is "*" or "+" or "0.0.0.0" or "[::]" or "::")
        {
            return false;
        }

        if (value.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var unbracketed = value.StartsWith('[') && value.EndsWith(']')
            ? value[1..^1]
            : value;

        return IPAddress.TryParse(unbracketed, out var address) && IPAddress.IsLoopback(address);
    }

    private static string? Value(IReadOnlyDictionary<string, string?> environment, string key)
    {
        if (!environment.TryGetValue(key, out var value))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static IReadOnlyDictionary<string, string?> CurrentEnvironment() =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [BindVariable] = Environment.GetEnvironmentVariable(BindVariable),
            [PortVariable] = Environment.GetEnvironmentVariable(PortVariable),
            [TokenVariable] = Environment.GetEnvironmentVariable(TokenVariable),
            [TokenFileVariable] = Environment.GetEnvironmentVariable(TokenFileVariable),
            [UnixSocketVariable] = Environment.GetEnvironmentVariable(UnixSocketVariable),
            [UnixSocketModeVariable] = Environment.GetEnvironmentVariable(UnixSocketModeVariable),
        };
}

public sealed class HttpTransportConfigurationException : Exception
{
    public HttpTransportConfigurationException(string message) : base(message)
    {
    }
}

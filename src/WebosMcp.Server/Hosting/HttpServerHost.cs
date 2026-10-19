using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebosMcp.Server.Configuration;

namespace WebosMcp.Server.Hosting;

public static class HttpServerHost
{
    /// <summary>
    /// Builds the Streamable HTTP host. <paramref name="configureBuilder"/> lets
    /// the test suite substitute an in-memory server; production passes null.
    /// Throws <see cref="HttpTransportConfigurationException"/> when the unix
    /// socket path cannot be used.
    /// </summary>
    public static WebApplication Build(
        HttpTransportSettings settings,
        string[] args,
        Action<WebApplicationBuilder>? configureBuilder = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
        builder.Logging.AddSecretSafeFilters();

        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<UnixSocketGate>();
        builder.Services.AddWebosMcp(builder.Configuration);

        // The same shared tool layer stdio serves — discovered from this assembly.
        builder.Services
            .AddMcpServer(options => options.ServerInfo = new() { Name = "webos-mcp", Version = "1.0.0" })
            .WithHttpTransport()
            .AddWebosMcpTools(builder.Configuration);

        configureBuilder?.Invoke(builder);

        if (configureBuilder is null)
        {
            if (settings.UnixSocketPath is { } socketPath)
            {
                // Endpoints configured in code replace any URL from the environment, so
                // the socket is the only listener: no TCP port opens.
                PrepareUnixSocketPath(socketPath);
                builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenUnixSocket(socketPath));
            }
            else
            {
                builder.WebHost.UseUrls(settings.Url);
            }
        }

        var app = builder.Build();

        // Nothing is served on a unix socket until its file mode has been applied.
        app.UseMiddleware<UnixSocketGateMiddleware>();

        // Auth runs ahead of everything, including the MCP endpoint mapping.
        app.UseMiddleware<BearerTokenMiddleware>();
        app.MapMcp();

        return app;
    }

    /// <summary>
    /// Starts the host. In unix-socket mode the configured file mode is applied
    /// right after the bind, and only then are requests served.
    /// </summary>
    public static async Task StartAsync(WebApplication app, HttpTransportSettings settings, CancellationToken cancellationToken)
    {
        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        if (settings.UnixSocketPath is { } socketPath && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(socketPath, settings.UnixSocketMode);
        }

        app.Services.GetRequiredService<UnixSocketGate>().Open();
    }

    /// <summary>
    /// Checks the socket's directory and clears a socket left by a previous run.
    /// Only a socket is ever removed: a regular file, directory or symlink at the
    /// path is an operator error and fails startup.
    /// </summary>
    internal static void PrepareUnixSocketPath(string socketPath)
    {
        var directory = Path.GetDirectoryName(socketPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            throw new HttpTransportConfigurationException(
                $"{HttpTransportSettings.UnixSocketVariable} points into '{directory}', which does not exist.");
        }

        if (!IsWritableDirectory(directory))
        {
            throw new HttpTransportConfigurationException(
                $"{HttpTransportSettings.UnixSocketVariable} points into '{directory}', which this process cannot write to.");
        }

        var type = FileTypeOf(socketPath);
        if (type is null)
        {
            return;
        }

        if (type != SocketType)
        {
            throw new HttpTransportConfigurationException(
                $"Refusing to start: '{socketPath}' exists and is not a socket. " +
                "Only a leftover socket is removed; move whatever is at that path, or choose another path.");
        }

        File.Delete(socketPath);
    }

    private static bool IsWritableDirectory(string directory)
    {
        var probe = Path.Combine(directory, $".webos-mcp-write-probe-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    // The runtime's own portable lstat: libSystem.Native ships with every .NET runtime
    // on Linux and macOS, and File/Directory use it internally. Its FileStatus struct
    // begins with Flags then Mode, and Mode carries portable S_IF* type bits. .NET has
    // no public API that tells a socket from a regular file.
    private const int FileTypeMask = 0xF000;
    private const int SocketType = 0xC000;

    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct FileStatusPrefix
    {
        public int Flags;
        public int Mode;
    }

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_LStat", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int LStat(string path, out FileStatusPrefix status);

    /// <summary>The S_IF* type bits of the path itself (symlinks not followed), or null when nothing is there.</summary>
    private static int? FileTypeOf(string path)
    {
        try
        {
            return LStat(path, out var status) == 0 ? status.Mode & FileTypeMask : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            if (!File.Exists(path) && !Directory.Exists(path) && new FileInfo(path).LinkTarget is null)
            {
                return null;
            }

            throw new HttpTransportConfigurationException(
                $"Refusing to start: '{path}' exists and this runtime cannot tell whether it is a socket. Remove it.");
        }
    }
}

/// <summary>Closed until the unix socket's file mode is applied; always open for a TCP listener.</summary>
public sealed class UnixSocketGate
{
    private volatile bool _open;

    public UnixSocketGate(HttpTransportSettings settings) => _open = settings.UnixSocketPath is null;

    public bool IsOpen => _open;

    public void Open() => _open = true;
}

/// <summary>Answers 503 to anything that reaches a unix socket before its mode is applied.</summary>
public sealed class UnixSocketGateMiddleware(RequestDelegate next, UnixSocketGate gate)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!gate.IsOpen)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}

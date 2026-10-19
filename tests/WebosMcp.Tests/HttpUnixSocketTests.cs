using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using WebosMcp.Server.Configuration;
using WebosMcp.Server.Hosting;
using Xunit;

namespace WebosMcp.Tests;

/// <summary>
/// The opt-in unix-socket listener: no TCP port at all, access governed by the
/// socket's file mode and directory. Real Kestrel on a temporary directory.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class HttpUnixSocketTests : IDisposable
{
    private const string Token = "s3cret";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wm-{Guid.NewGuid():N}"[..11]);

    public HttpUnixSocketTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string SocketPath => Path.Combine(_dir, "http.sock");

    private static IReadOnlyDictionary<string, string?> Env(params (string Key, string? Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

    private HttpTransportSettings Resolve(string mode) => HttpTransportSettings.Resolve(Env(
        (HttpTransportSettings.UnixSocketVariable, SocketPath),
        (HttpTransportSettings.UnixSocketModeVariable, mode),
        (HttpTransportSettings.TokenVariable, Token)));

    private HttpClient ClientFor(string path) => new(new SocketsHttpHandler
    {
        ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct);
            return new NetworkStream(socket, ownsSocket: true);
        },
    })
    {
        BaseAddress = new Uri("http://localhost"),
    };

    [Fact]
    public async Task The_socket_gets_the_configured_mode_and_no_TCP_listener_opens()
    {
        var settings = Resolve("0660");

        // A URL from the environment must not add a TCP listener next to the socket.
        await using var app = HttpServerHost.Build(settings, ["--urls", "http://127.0.0.1:0"]);
        await HttpServerHost.StartAsync(app, settings, CancellationToken.None);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite,
            File.GetUnixFileMode(SocketPath));

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        Assert.Equal([$"http://unix:{SocketPath}"], addresses);

        using var client = ClientFor(SocketPath);
        using var anonymous = await client.PostAsync("/", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/") { Content = new StringContent("{}") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var authenticated = await client.SendAsync(request);
        Assert.NotEqual(HttpStatusCode.Unauthorized, authenticated.StatusCode);
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, authenticated.StatusCode);

        await app.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void The_default_mode_is_owner_only()
    {
        var settings = HttpTransportSettings.Resolve(Env((HttpTransportSettings.UnixSocketVariable, SocketPath)));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, settings.UnixSocketMode);
        Assert.Equal($"http://unix:{SocketPath}", settings.Url);
    }

    [Theory]
    [InlineData("0644")]
    [InlineData("0666")]
    [InlineData("660")]
    [InlineData("0700")]
    public void A_mode_other_than_0600_or_0660_fails_startup(string mode)
    {
        Assert.Throws<HttpTransportConfigurationException>(() => Resolve(mode));
    }

    [Theory]
    [InlineData(HttpTransportSettings.BindVariable, "127.0.0.1")]
    [InlineData(HttpTransportSettings.PortVariable, "8765")]
    public void A_socket_together_with_a_TCP_bind_or_port_fails_startup(string key, string value)
    {
        var error = Assert.Throws<HttpTransportConfigurationException>(() => HttpTransportSettings.Resolve(Env(
            (HttpTransportSettings.UnixSocketVariable, SocketPath),
            (key, value))));

        Assert.Contains(HttpTransportSettings.UnixSocketVariable, error.Message);
    }

    [Fact]
    public void A_socket_with_an_empty_bind_and_port_starts()
    {
        var settings = HttpTransportSettings.Resolve(Env(
            (HttpTransportSettings.UnixSocketVariable, SocketPath),
            (HttpTransportSettings.BindVariable, ""),
            (HttpTransportSettings.PortVariable, "")));

        Assert.Equal(SocketPath, settings.UnixSocketPath);
    }

    [Fact]
    public void A_relative_socket_path_or_a_mode_without_a_socket_fails_startup()
    {
        Assert.Throws<HttpTransportConfigurationException>(() => HttpTransportSettings.Resolve(Env(
            (HttpTransportSettings.UnixSocketVariable, "run/http.sock"))));

        Assert.Throws<HttpTransportConfigurationException>(() => HttpTransportSettings.Resolve(Env(
            (HttpTransportSettings.UnixSocketModeVariable, "0660"))));
    }

    [Fact]
    public void A_regular_file_at_the_socket_path_fails_startup_and_is_left_alone()
    {
        File.WriteAllText(SocketPath, "not a socket");

        Assert.Throws<HttpTransportConfigurationException>(() => HttpServerHost.Build(Resolve("0600"), []));
        Assert.Equal("not a socket", File.ReadAllText(SocketPath));
    }

    [Fact]
    public void A_directory_or_symlink_at_the_socket_path_fails_startup()
    {
        Directory.CreateDirectory(SocketPath);
        Assert.Throws<HttpTransportConfigurationException>(() => HttpServerHost.Build(Resolve("0600"), []));
        Directory.Delete(SocketPath);

        var target = Path.Combine(_dir, "target");
        File.WriteAllText(target, "x");
        File.CreateSymbolicLink(SocketPath, target);
        Assert.Throws<HttpTransportConfigurationException>(() => HttpServerHost.Build(Resolve("0600"), []));
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void A_missing_socket_directory_fails_startup()
    {
        var settings = HttpTransportSettings.Resolve(Env(
            (HttpTransportSettings.UnixSocketVariable, Path.Combine(_dir, "missing", "http.sock"))));

        Assert.Throws<HttpTransportConfigurationException>(() => HttpServerHost.Build(settings, []));
    }

    [Fact]
    public async Task A_stale_socket_from_a_previous_run_is_replaced()
    {
        CreateStaleSocket(SocketPath);
        Assert.True(File.Exists(SocketPath));

        var settings = Resolve("0600");
        await using var app = HttpServerHost.Build(settings, []);
        await HttpServerHost.StartAsync(app, settings, CancellationToken.None);

        using var client = ClientFor(SocketPath);
        using var response = await client.PostAsync("/", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await app.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Leaves a socket file with nothing listening, as a crashed run does. The handle
    /// is closed directly so the managed socket does not unlink its own path.
    /// </summary>
    private static void CreateStaleSocket(string path)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path));
        socket.SafeHandle.Dispose();
        GC.SuppressFinalize(socket);
    }
}

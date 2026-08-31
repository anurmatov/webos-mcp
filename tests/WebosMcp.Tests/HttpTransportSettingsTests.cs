using WebosMcp.Server.Configuration;
using Xunit;

namespace WebosMcp.Tests;

/// <summary>
/// The HTTP bind/token invariant. This is the highest-severity failure mode for
/// the HTTP transport, so it is tested as configuration policy in its own right.
/// </summary>
public sealed class HttpTransportSettingsTests
{
    private static Dictionary<string, string?> Env(params (string Key, string? Value)[] entries)
    {
        var dictionary = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [HttpTransportSettings.BindVariable] = null,
            [HttpTransportSettings.PortVariable] = null,
            [HttpTransportSettings.TokenVariable] = null,
            [HttpTransportSettings.TokenFileVariable] = null,
        };

        foreach (var (key, value) in entries)
        {
            dictionary[key] = value;
        }

        return dictionary;
    }

    [Fact]
    public void The_default_bind_is_loopback_only_and_needs_no_token()
    {
        var settings = HttpTransportSettings.Resolve(Env());

        Assert.Equal("127.0.0.1", settings.BindAddress);
        Assert.Equal(8765, settings.Port);
        Assert.False(settings.RequiresAuth);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("*")]
    [InlineData("192.0.2.10")]
    [InlineData("[::]")]
    public void A_non_loopback_bind_without_a_token_refuses_to_start(string bind)
    {
        var ex = Assert.Throws<HttpTransportConfigurationException>(
            () => HttpTransportSettings.Resolve(Env((HttpTransportSettings.BindVariable, bind))));

        Assert.Contains("Refusing to start", ex.Message, StringComparison.Ordinal);
        Assert.Contains(HttpTransportSettings.TokenVariable, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_loopback_bind_with_a_token_is_allowed()
    {
        var settings = HttpTransportSettings.Resolve(Env(
            (HttpTransportSettings.BindVariable, "0.0.0.0"),
            (HttpTransportSettings.TokenVariable, "s3cret")));

        Assert.Equal("0.0.0.0", settings.BindAddress);
        Assert.True(settings.RequiresAuth);
    }

    [Fact]
    public void An_empty_token_variable_counts_as_no_token()
    {
        Assert.Throws<HttpTransportConfigurationException>(
            () => HttpTransportSettings.Resolve(Env(
                (HttpTransportSettings.BindVariable, "0.0.0.0"),
                (HttpTransportSettings.TokenVariable, "   "))));
    }

    [Fact]
    public void A_token_file_satisfies_the_non_loopback_requirement()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "  file-token\n");

            var settings = HttpTransportSettings.Resolve(Env(
                (HttpTransportSettings.BindVariable, "0.0.0.0"),
                (HttpTransportSettings.TokenFileVariable, path)));

            Assert.Equal("file-token", settings.Token);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_token_file_wins_over_the_inline_variable()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "from-file");

            var settings = HttpTransportSettings.Resolve(Env(
                (HttpTransportSettings.TokenFileVariable, path),
                (HttpTransportSettings.TokenVariable, "from-env")));

            Assert.Equal("from-file", settings.Token);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_missing_token_file_is_a_startup_failure()
    {
        var ex = Assert.Throws<HttpTransportConfigurationException>(
            () => HttpTransportSettings.Resolve(Env(
                (HttpTransportSettings.TokenFileVariable, "/nonexistent/token"))));

        Assert.Contains("does not exist", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_token_file_is_a_startup_failure()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "   ");

            var ex = Assert.Throws<HttpTransportConfigurationException>(
                () => HttpTransportSettings.Resolve(Env((HttpTransportSettings.TokenFileVariable, path))));

            Assert.Contains("empty", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("not-a-port")]
    [InlineData("0")]
    [InlineData("70000")]
    public void An_invalid_port_is_a_startup_failure(string port)
    {
        Assert.Throws<HttpTransportConfigurationException>(
            () => HttpTransportSettings.Resolve(Env((HttpTransportSettings.PortVariable, port))));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.5.5.5", true)]
    [InlineData("localhost", true)]
    [InlineData("::1", true)]
    [InlineData("[::1]", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::", false)]
    [InlineData("*", false)]
    [InlineData("192.0.2.10", false)]
    [InlineData("", false)]
    public void Loopback_classification_is_explicit(string bind, bool expected) =>
        Assert.Equal(expected, HttpTransportSettings.IsLoopback(bind));
}

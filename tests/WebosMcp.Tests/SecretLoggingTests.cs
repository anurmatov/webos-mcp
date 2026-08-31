using Microsoft.Extensions.Logging;
using WebosMcp.Application;
using WebosMcp.Infrastructure;
using WebosMcp.Server.Hosting;
using WebosMcp.Tests.Fakes;
using Xunit;

namespace WebosMcp.Tests;

/// <summary>
/// The Lounge token must never reach a log line.
///
/// It travels as a QUERY STRING parameter (loungeIdToken=…), and HttpClient's own
/// logging writes the full request URI at Information. No code of ours has to log
/// it for it to leak — using HttpClient normally is enough. That makes this a
/// property of the host wiring, not of any one class.
/// </summary>
public sealed class SecretLoggingTests
{
    private const string Token = "SECRET-LOUNGE-TOKEN-must-never-appear";

    private static (CapturingLoggerProvider Capture, ILoggerFactory Factory) BuildWithPolicy()
    {
        var capture = new CapturingLoggerProvider();

        var factory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(capture);

            // The exact policy both hosts and the CLI apply.
            logging.AddSecretSafeFilters();
        });

        return (capture, factory);
    }

    [Theory]
    [InlineData("System.Net.Http.HttpClient.ILoungeClient.LogicalHandler")]
    [InlineData("System.Net.Http.HttpClient.ILoungeClient.ClientHandler")]
    [InlineData("Microsoft.Extensions.Http.DefaultHttpClientFactory")]
    public void HttpClient_request_uris_are_filtered_out_before_they_can_carry_the_token(string category)
    {
        var (capture, factory) = BuildWithPolicy();

        // Exactly what HttpClient logging does: the whole URI at Information.
        factory.CreateLogger(category).LogInformation(
            "Sending HTTP request POST https://www.youtube.com/api/lounge/bc/bind?loungeIdToken={Token}",
            Token);

        Assert.DoesNotContain(capture.Lines, line => line.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public void The_policy_does_not_silence_genuine_warnings()
    {
        // Filtering to Warning must not be a blanket mute — a failing request still
        // has to be visible, or this fix trades a leak for an outage nobody can see.
        var (capture, factory) = BuildWithPolicy();

        factory.CreateLogger("System.Net.Http.HttpClient.ILoungeClient.LogicalHandler")
            .LogWarning("request failed");

        Assert.Contains(capture.Lines, line => line.Contains("request failed", StringComparison.Ordinal));
    }

    [Fact]
    public void Ordinary_application_logging_is_untouched()
    {
        var (capture, factory) = BuildWithPolicy();

        factory.CreateLogger("WebosMcp.Application.TvControlService").LogInformation("hello");

        Assert.Contains(capture.Lines, line => line.Contains("hello", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_client_itself_never_writes_the_token_to_a_log_line()
    {
        // Defence in depth: even with NO filtering at all, our own code must not
        // put the token in a message. The filter covers HttpClient; this covers us.
        var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(capture);
        });

        var handler = new ScriptedDialHttpHandler(
            System.Net.HttpStatusCode.OK,
            $$"""{"screens":[{"screenId":"screen-1","loungeToken":"{{Token}}"}]}""");

        var client = new LoungeClient(
            new HttpClient(handler),
            Microsoft.Extensions.Options.Options.Create(new WebosMcpOptions()),
            factory,
            factory.CreateLogger<LoungeClient>());

        // The bind response carries no session ids, so this connect fails — the path
        // that logs the most, and therefore the one most likely to leak.
        await client.ConnectAsync("screen-1", CancellationToken.None);

        Assert.DoesNotContain(capture.Lines, line => line.Contains(Token, StringComparison.Ordinal));
    }
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WebosMcp.Application;
using WebosMcp.Tests.Fakes;

namespace WebosMcp.Tests;

/// <summary>Wires the real application layer over fake transports.</summary>
public sealed class TestHarness
{
    public TestHarness(
        FakeSsapConnection? connection = null,
        Action<WebosMcpOptions>? configure = null,
        ILoggerFactory? loggerFactory = null)
    {
        Connection = connection ?? new FakeSsapConnection();
        Factory = new FakeSsapConnectionFactory().Enqueue(Connection);

        Options = new WebosMcpOptions
        {
            Host = "192.0.2.10",
            MacAddress = "00:11:22:33:44:55",
            BroadcastAddress = "192.0.2.255",
            RequestTimeoutSeconds = 5,
            PowerOnVerifyTimeoutSeconds = 12,
            PowerOnPollIntervalSeconds = 3,
            FallbackStepDelayMilliseconds = 0,
            LaunchVerifyTimeoutSeconds = 6,
            LaunchPollIntervalSeconds = 2,
            LoungeVerifyTimeoutSeconds = 1,
            LoungeSubscribeTimeoutSeconds = 1,
        };

        configure?.Invoke(Options);

        LoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        KeyStore = new FakeClientKeyStore();
        Wol = new FakeWolSender();
        Delay = new InstantDelayProvider();
        Dial = new FakeDialClient();
        Lounge = new FakeLoungeClient();

        var wrapped = Microsoft.Extensions.Options.Options.Create(Options);

        Session = new TvSession(Factory, KeyStore, wrapped, LoggerFactory.CreateLogger<TvSession>());
        Control = new TvControlService(
            Session, Delay, Dial, Lounge, wrapped, LoggerFactory.CreateLogger<TvControlService>());
        Power = new PowerService(Wol, Control, Delay, wrapped, LoggerFactory.CreateLogger<PowerService>());
    }

    public FakeSsapConnection Connection { get; }

    public FakeSsapConnectionFactory Factory { get; }

    public WebosMcpOptions Options { get; }

    public FakeClientKeyStore KeyStore { get; }

    public FakeWolSender Wol { get; }

    public InstantDelayProvider Delay { get; }

    public FakeDialClient Dial { get; }

    public FakeLoungeClient Lounge { get; }

    public ILoggerFactory LoggerFactory { get; }

    public TvSession Session { get; }

    public TvControlService Control { get; }

    public PowerService Power { get; }
}

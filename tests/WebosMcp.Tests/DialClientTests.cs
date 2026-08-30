using WebosMcp.Infrastructure;
using Xunit;

namespace WebosMcp.Tests;

/// <summary>
/// The DIAL response parser. The rest of the DIAL client is network I/O and is
/// covered through IDialClient fakes; this pins the one piece of real parsing,
/// because misreading "installable" as installed would put us straight back to
/// launching something that cannot run.
/// </summary>
public sealed class DialClientTests
{
    private const string Ns = "urn:dial-multiscreen-org:schemas:dial";

    [Fact]
    public void A_stopped_but_installed_app_is_installed_and_not_running()
    {
        var status = DialClient.ParseAppStatus("YouTube",
            $"""<service xmlns="{Ns}"><name>YouTube</name><options allowStop="true"/><state>stopped</state></service>""");

        Assert.NotNull(status);
        Assert.True(status!.Installed);
        Assert.False(status.IsRunning);
        Assert.Equal("YouTube", status.Name);
    }

    [Theory]
    [InlineData("running")]
    [InlineData("starting")]
    public void A_running_or_starting_app_reports_running(string state)
    {
        var status = DialClient.ParseAppStatus("YouTube",
            $"""<service xmlns="{Ns}"><name>YouTube</name><state>{state}</state></service>""");

        Assert.True(status!.IsRunning);
        Assert.True(status.Installed);
    }

    [Fact]
    public void An_installable_app_is_NOT_installed()
    {
        // DIAL signals "not installed, but here is where to get it" as an
        // installable=<url> state. Treating that as installed would mean
        // launching something that cannot run.
        var status = DialClient.ParseAppStatus("YouTube",
            $"""<service xmlns="{Ns}"><name>YouTube</name><state>installable=https://example.com/app</state></service>""");

        Assert.NotNull(status);
        Assert.False(status!.Installed);
    }

    [Fact]
    public void A_response_with_no_state_is_not_a_usable_status()
    {
        Assert.Null(DialClient.ParseAppStatus("YouTube",
            $"""<service xmlns="{Ns}"><name>YouTube</name></service>"""));
    }

    [Theory]
    [InlineData("not xml at all")]
    [InlineData("<service><state>running")]
    [InlineData("")]
    public void Malformed_xml_is_rejected_rather_than_throwing(string body)
    {
        Assert.Null(DialClient.ParseAppStatus("YouTube", body));
    }
}

using WebosMcp.Application;
using WebosMcp.Domain;
using Xunit;

namespace WebosMcp.Tests;

/// <summary>One rejection path per validated field, plus proof that a rejected input never reaches the TV.</summary>
public sealed class ValidationTests
{
    [Theory]
    [InlineData("http://example.com")]
    [InlineData("ftp://example.com/file")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Non_https_urls_are_rejected(string url)
    {
        var ex = Assert.Throws<TvException>(() => InputValidation.ValidateHttpsUrl(url));
        Assert.Equal(TvErrorCode.InvalidInput, ex.Code);
    }

    [Fact]
    public void Https_urls_are_accepted()
    {
        var uri = InputValidation.ValidateHttpsUrl("https://example.com/watch?v=abc");
        Assert.Equal("example.com", uri.Host);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("bad/slash")]
    [InlineData("../escape")]
    public void Malformed_app_ids_are_rejected(string appId) =>
        Assert.Equal(TvErrorCode.InvalidInput, Assert.Throws<TvException>(
            () => InputValidation.ValidateAppId(appId)).Code);

    [Fact]
    public void Well_formed_app_ids_are_accepted() =>
        Assert.Equal("com.webos.app.browser", InputValidation.ValidateAppId("com.webos.app.browser"));

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public void Out_of_range_volume_is_rejected(int volume) =>
        Assert.Equal(TvErrorCode.InvalidInput, Assert.Throws<TvException>(
            () => InputValidation.ValidateVolume(volume)).Code);

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(100)]
    public void In_range_volume_is_accepted(int volume) =>
        Assert.Equal(volume, InputValidation.ValidateVolume(volume));

    [Theory]
    [InlineData(501)]
    [InlineData(-501)]
    public void Unbounded_pointer_deltas_are_rejected(int delta) =>
        Assert.Equal(TvErrorCode.InvalidInput, Assert.Throws<TvException>(
            () => InputValidation.ValidatePointerDelta(delta, "x")).Code);

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void Out_of_range_repeat_is_rejected(int repeat) =>
        Assert.Equal(TvErrorCode.InvalidInput, Assert.Throws<TvException>(
            () => InputValidation.ValidateRepeat(repeat)).Code);

    [Fact]
    public void Overlong_text_is_rejected() =>
        Assert.Equal(TvErrorCode.InvalidInput, Assert.Throws<TvException>(
            () => InputValidation.ValidateText(new string('a', 513))).Code);

    [Fact]
    public void Control_characters_in_text_are_rejected() =>
        Assert.Equal(TvErrorCode.InvalidInput, Assert.Throws<TvException>(
            () => InputValidation.ValidateText("hello\u0007world")).Code);

    [Fact]
    public void Ordinary_text_is_accepted() =>
        Assert.Equal("hello world", InputValidation.ValidateText("hello world"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_toast_message_is_rejected(string message) =>
        Assert.Equal(TvErrorCode.InvalidInput, Assert.Throws<TvException>(
            () => InputValidation.ValidateToastMessage(message)).Code);

    [Fact]
    public void Overlong_toast_message_is_rejected() =>
        Assert.Equal(TvErrorCode.InvalidInput, Assert.Throws<TvException>(
            () => InputValidation.ValidateToastMessage(new string('a', 513))).Code);

    [Fact]
    public void Empty_search_query_is_rejected() =>
        Assert.Equal(TvErrorCode.InvalidInput, Assert.Throws<TvException>(
            () => InputValidation.ValidateSearchQuery("  ")).Code);

    [Theory]
    [InlineData("abc")]
    [InlineData("7-1-2")]
    [InlineData("x7")]
    public void Malformed_channel_numbers_are_rejected(string channel) =>
        Assert.Equal(TvErrorCode.InvalidInput, Assert.Throws<TvException>(
            () => InputValidation.ValidateChannelNumber(channel)).Code);

    [Theory]
    [InlineData("7")]
    [InlineData("7-1")]
    public void Well_formed_channel_numbers_are_accepted(string channel) =>
        Assert.Equal(channel, InputValidation.ValidateChannelNumber(channel));

    [Theory]
    [InlineData("")]
    [InlineData("bad id")]
    [InlineData("bad/slash")]
    public void Malformed_input_ids_are_rejected(string inputId) =>
        Assert.Equal(TvErrorCode.InvalidInput, Assert.Throws<TvException>(
            () => InputValidation.ValidateInputId(inputId)).Code);

    [Theory]
    [InlineData("dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    public void YouTube_ids_and_urls_normalise_to_a_bare_id(string input, string expected) =>
        Assert.Equal(expected, InputValidation.ValidateYouTubeVideoId(input));

    [Theory]
    [InlineData("https://example.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("short")]
    [InlineData("")]
    public void Non_youtube_video_references_are_rejected(string input) =>
        Assert.Equal(TvErrorCode.InvalidInput, Assert.Throws<TvException>(
            () => InputValidation.ValidateYouTubeVideoId(input)).Code);

    [Fact]
    public async Task A_rejected_input_never_opens_a_connection()
    {
        var harness = new TestHarness();

        await Assert.ThrowsAsync<TvException>(
            () => harness.Control.OpenUrlAsync("http://example.com", CancellationToken.None));

        Assert.Equal(0, harness.Factory.CreateCount);
        Assert.Empty(harness.Connection.Calls);
    }

    [Fact]
    public void Every_remote_button_maps_to_a_wire_name()
    {
        foreach (var button in Enum.GetValues<RemoteButton>())
        {
            Assert.False(string.IsNullOrWhiteSpace(button.ToWireName()));
        }
    }

    [Theory]
    [InlineData("00:11:22:33:44:55")]
    [InlineData("00-11-22-33-44-55")]
    [InlineData("001122334455")]
    public void Mac_addresses_parse_in_the_common_formats(string mac) =>
        Assert.Equal("001122334455", WebosMcpOptions.ParseMac(mac).ToString());

    [Theory]
    [InlineData("00:11:22:33:44")]
    [InlineData("zz:11:22:33:44:55")]
    [InlineData("")]
    public void Malformed_mac_addresses_are_rejected(string mac) =>
        Assert.Equal(TvErrorCode.InvalidInput, Assert.Throws<TvException>(
            () => WebosMcpOptions.ParseMac(mac)).Code);
}

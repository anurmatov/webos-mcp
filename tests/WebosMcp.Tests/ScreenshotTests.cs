using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using WebosMcp.Application;
using WebosMcp.Domain;
using WebosMcp.Infrastructure;
using WebosMcp.Server.Hosting;
using WebosMcp.Server.Tools;
using WebosMcp.Tests.Fakes;
using Xunit;

namespace WebosMcp.Tests;

/// <summary>
/// The capture path, end to end, with no TV and no socket.
///
/// The thing under test is mostly NOT "does a screenshot come back" — it is that
/// the TV-supplied <c>imageUri</c> is treated as untrusted input, and that a body
/// which is not really an image is never reported as a successful capture.
/// </summary>
public sealed class ScreenshotTests
{
    private const string TvHost = "192.0.2.10";
    private const string CaptureUri = $"http://{TvHost}:9080/tmp/capture.jpg";

    private static TestHarness HarnessWith(string ssapResponse, ILoggerFactory? loggerFactory = null)
    {
        var connection = new FakeSsapConnection();
        connection.Respond(SsapUri.ExecuteOneShot, ssapResponse);
        return new TestHarness(connection, loggerFactory: loggerFactory);
    }

    private static TestHarness HarnessAnnouncing(string imageUri, ILoggerFactory? loggerFactory = null) =>
        HarnessWith($$"""{"returnValue":true,"imageUri":"{{imageUri}}"}""", loggerFactory);

    private static async Task<TvErrorCode> CodeOf(TestHarness harness)
    {
        var ex = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.CaptureScreenshotAsync(CancellationToken.None));
        return ex.Code;
    }

    // ------------------------------------------------------------- the endpoint

    [Fact]
    public void The_capture_endpoint_is_a_fixed_constant_in_the_closed_ssap_list()
    {
        // The value is pinned here rather than only referenced, so a rename or a
        // typo in the closed list is a test failure and not a silent 404 from the TV.
        Assert.Equal("ssap://tv/executeOneShot", SsapUri.ExecuteOneShot);
    }

    [Fact]
    public async Task A_capture_calls_only_the_one_ssap_endpoint()
    {
        var harness = HarnessAnnouncing(CaptureUri);

        await harness.Control.CaptureScreenshotAsync(CancellationToken.None);

        Assert.Equal([SsapUri.ExecuteOneShot], harness.Connection.RequestUris);
    }

    // ------------------------------------------------------------ happy path

    [Fact]
    public async Task A_valid_same_host_jpeg_is_returned_with_its_sniffed_mime_type()
    {
        var harness = HarnessAnnouncing(CaptureUri);

        var shot = await harness.Control.CaptureScreenshotAsync(CancellationToken.None);

        Assert.Equal("image/jpeg", shot.MimeType);
        Assert.Equal(ImageFixtures.Jpeg, shot.Bytes.ToArray());
        Assert.Equal(new Uri(CaptureUri), Assert.Single(harness.Downloader.Requested));
    }

    [Fact]
    public async Task A_png_capture_is_identified_by_content_not_by_the_uri_extension()
    {
        // The URI says .jpg; the bytes say PNG. The bytes win — the alternative is
        // trusting a label the TV supplies about a body it also supplies.
        var harness = HarnessAnnouncing(CaptureUri);
        harness.Downloader.Body = ImageFixtures.Png;

        var shot = await harness.Control.CaptureScreenshotAsync(CancellationToken.None);

        Assert.Equal("image/png", shot.MimeType);
    }

    [Fact]
    public async Task An_https_capture_on_the_selected_host_is_accepted()
    {
        // ValidateHttpsUrl is deliberately NOT reused here, so https must still work:
        // the rule is a pinned host, not a pinned scheme.
        var harness = HarnessAnnouncing($"https://{TvHost}/capture.png");

        var shot = await harness.Control.CaptureScreenshotAsync(CancellationToken.None);

        Assert.Equal("image/jpeg", shot.MimeType);
    }

    // ------------------------------------------------------- ssap-level refusal

    [Fact]
    public async Task An_unsupported_capture_endpoint_surfaces_TV_UNSUPPORTED_CAPABILITY()
    {
        var connection = new FakeSsapConnection();
        connection.Fail(SsapUri.ExecuteOneShot, TvException.Unsupported("frame capture"));
        var harness = new TestHarness(connection);

        Assert.Equal(TvErrorCode.TvUnsupportedCapability, await CodeOf(harness));
        Assert.Empty(harness.Downloader.Requested);
    }

    [Theory]
    [InlineData("404 no such service")]
    [InlineData("Method not supported")]
    public void An_ssap_error_naming_an_absent_method_maps_to_TV_UNSUPPORTED_CAPABILITY(string detail)
    {
        // The mapping that turns the TV's own refusal into the honest-unsupported
        // code lives in the SSAP layer; this pins it for the capture wording too.
        // Capture runs as an ordinary command, so the REQUEST-frame mapper is the
        // one that applies — the registration-frame mapper reads the same wording
        // differently, which is the point of them being separate.
        Assert.Equal(
            TvErrorCode.TvUnsupportedCapability,
            SsapWebSocketConnection.MapRequestError(detail).Code);
    }

    // ------------------------------------------------- untrusted imageUri rules

    [Fact]
    public async Task A_response_with_no_imageUri_is_INVALID_INPUT()
    {
        var harness = HarnessWith("""{"returnValue":true}""");

        Assert.Equal(TvErrorCode.InvalidInput, await CodeOf(harness));
        Assert.Empty(harness.Downloader.Requested);
    }

    [Theory]
    [InlineData("not a url at all")]
    [InlineData("/tmp/capture.jpg")]
    [InlineData("http://")]
    public async Task A_malformed_imageUri_is_INVALID_INPUT(string imageUri)
    {
        var harness = HarnessAnnouncing(imageUri);

        Assert.Equal(TvErrorCode.InvalidInput, await CodeOf(harness));
        Assert.Empty(harness.Downloader.Requested);
    }

    [Theory]
    [InlineData("ftp://192.0.2.10/capture.jpg")]
    [InlineData("file:///tmp/capture.jpg")]
    [InlineData("ws://192.0.2.10/capture.jpg")]
    public async Task A_disallowed_scheme_is_INVALID_INPUT(string imageUri)
    {
        var harness = HarnessAnnouncing(imageUri);

        Assert.Equal(TvErrorCode.InvalidInput, await CodeOf(harness));
        Assert.Empty(harness.Downloader.Requested);
    }

    [Theory]
    [InlineData("http://198.51.100.7/capture.jpg")]
    [InlineData("https://example.invalid/capture.jpg")]
    public async Task An_imageUri_pointing_at_another_host_is_INVALID_INPUT(string imageUri)
    {
        var harness = HarnessAnnouncing(imageUri);

        Assert.Equal(TvErrorCode.InvalidInput, await CodeOf(harness));

        // The refusal happens BEFORE any request goes out. A rejection that still
        // fetched would defeat the point of the rule.
        Assert.Empty(harness.Downloader.Requested);
    }

    [Fact]
    public async Task An_imageUri_carrying_userinfo_is_INVALID_INPUT()
    {
        var harness = HarnessAnnouncing($"http://user:pass@{TvHost}/capture.jpg");

        Assert.Equal(TvErrorCode.InvalidInput, await CodeOf(harness));
        Assert.Empty(harness.Downloader.Requested);
    }

    [Fact]
    public void Host_pinning_accepts_the_selected_tv_and_nothing_else()
    {
        // This predicate also gates the narrowly-scoped TLS allowance, so a wrong
        // answer here would widen certificate acceptance, not only the download.
        var options = new WebosMcpOptions { Host = TvHost };

        Assert.True(ScreenshotPolicy.IsSelectedTvHost(new Uri($"http://{TvHost}:9080/x"), options));
        Assert.True(ScreenshotPolicy.IsSelectedTvHost(new Uri($"https://{TvHost}/x"), options));
        Assert.False(ScreenshotPolicy.IsSelectedTvHost(new Uri("http://198.51.100.7/x"), options));
        Assert.False(ScreenshotPolicy.IsSelectedTvHost(new Uri("http://example.invalid/x"), options));
        Assert.False(ScreenshotPolicy.IsSelectedTvHost(null, options));

        // No selected TV means nothing is trusted, rather than everything.
        Assert.False(ScreenshotPolicy.IsSelectedTvHost(
            new Uri($"http://{TvHost}/x"), new WebosMcpOptions { Host = null }));
    }

    // --------------------------------------------------- body must be an image

    [Fact]
    public async Task An_empty_body_is_TV_ERROR()
    {
        var harness = HarnessAnnouncing(CaptureUri);
        harness.Downloader.Body = [];

        Assert.Equal(TvErrorCode.TvError, await CodeOf(harness));
    }

    [Fact]
    public async Task An_html_error_page_is_TV_ERROR_even_though_it_downloaded_fine()
    {
        var harness = HarnessAnnouncing(CaptureUri);
        harness.Downloader.Body = Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>404</body></html>");

        Assert.Equal(TvErrorCode.TvError, await CodeOf(harness));
    }

    [Fact]
    public async Task A_body_that_is_not_a_supported_image_is_TV_ERROR()
    {
        var harness = HarnessAnnouncing(CaptureUri);
        harness.Downloader.Body = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B];

        Assert.Equal(TvErrorCode.TvError, await CodeOf(harness));
    }

    [Fact]
    public void Content_sniffing_recognises_exactly_the_two_supported_formats()
    {
        Assert.Equal("image/jpeg", ScreenshotPolicy.DetectImageMimeType(ImageFixtures.Jpeg));
        Assert.Equal("image/png", ScreenshotPolicy.DetectImageMimeType(ImageFixtures.Png));

        // A GIF is a real image and still refused: the contract names two formats,
        // and quietly widening it would ship a MIME type the description does not
        // promise.
        Assert.Equal(
            TvErrorCode.TvError,
            Assert.Throws<TvException>(
                () => ScreenshotPolicy.DetectImageMimeType("GIF89a\u0000\u0000\u0000"u8)).Code);

        // WebP is refused deliberately: a RIFF length field can be made
        // self-consistent over arbitrary content, so it cannot be validated to the
        // standard the other two are held to.
        Assert.Equal(
            TvErrorCode.TvError,
            Assert.Throws<TvException>(
                () => ScreenshotPolicy.DetectImageMimeType("RIFFxxxxWEBPVP8 "u8)).Code);
    }

    // ------------------------------------------- completeness, not just a prefix

    [Fact]
    public void The_fixtures_really_are_images_and_not_magic_number_prefixes()
    {
        // Both were produced by a real encoder and decoded back to their original
        // pixels before being embedded. If this ever reduces to "starts with the
        // right bytes", every success test below stops proving anything.
        Assert.Equal<byte[]>([0xFF, 0xD8, 0xFF], ImageFixtures.Jpeg[..3]);
        Assert.Equal<byte[]>([0xFF, 0xD9], ImageFixtures.Jpeg[^2..]);
        Assert.True(ImageFixtures.Jpeg.Length > 500);

        Assert.Equal<byte[]>(
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], ImageFixtures.Png[..8]);
        Assert.Equal("IEND"u8.ToArray(), ImageFixtures.Png[^8..^4]);
    }

    [Fact]
    public async Task A_jpeg_cut_short_is_TV_ERROR_even_though_its_signature_is_valid()
    {
        // The failure a magic-number check cannot see: a real capture whose
        // connection dropped mid-body still starts with FF D8 FF. Reporting that as
        // a successful capture hands back an image that will not open.
        var harness = HarnessAnnouncing(CaptureUri);
        harness.Downloader.Body = ImageFixtures.TruncatedJpeg;

        Assert.Equal(TvErrorCode.TvError, await CodeOf(harness));
    }

    [Fact]
    public async Task A_png_missing_its_IEND_chunk_is_TV_ERROR()
    {
        var harness = HarnessAnnouncing(CaptureUri);
        harness.Downloader.Body = ImageFixtures.TruncatedPng;

        Assert.Equal(TvErrorCode.TvError, await CodeOf(harness));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(64)]
    public void A_bare_signature_with_no_body_is_rejected(int prefixLength)
    {
        Assert.Equal(
            TvErrorCode.TvError,
            Assert.Throws<TvException>(
                () => ScreenshotPolicy.DetectImageMimeType(ImageFixtures.Jpeg[..prefixLength])).Code);
    }

    [Fact]
    public void A_jpeg_that_is_corrupt_BETWEEN_a_valid_signature_and_a_valid_EOI_is_rejected()
    {
        var corrupt = ImageFixtures.MarkerCorruptedJpeg;

        // The fixture's whole point: it passes every check a bracket-based
        // validator can make. If these three assertions ever fail, the negative
        // control has stopped testing what it claims to.
        Assert.Equal<byte[]>([0xFF, 0xD8, 0xFF], corrupt[..3]);
        Assert.Equal<byte[]>([0xFF, 0xD9], corrupt[^2..]);
        Assert.Equal(ImageFixtures.Jpeg.Length, corrupt.Length);

        var ex = Assert.Throws<TvException>(() => ScreenshotPolicy.DetectImageMimeType(corrupt));

        Assert.Equal(TvErrorCode.TvError, ex.Code);
        Assert.Contains("length", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_png_whose_chunk_no_longer_matches_its_own_CRC_is_rejected()
    {
        var corrupt = ImageFixtures.BadCrcPng;

        // Same shape: signature intact, IEND intact, chunk lengths intact. Only
        // verifying the checksum catches it.
        Assert.Equal<byte[]>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], corrupt[..8]);
        Assert.Equal("IEND"u8.ToArray(), corrupt[^8..^4]);
        Assert.Equal(ImageFixtures.Png.Length, corrupt.Length);

        var ex = Assert.Throws<TvException>(() => ScreenshotPolicy.DetectImageMimeType(corrupt));

        Assert.Equal(TvErrorCode.TvError, ex.Code);
        Assert.Contains("CRC32", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_corrupt_capture_is_never_reported_as_a_successful_screenshot()
    {
        // End to end through the capture path, not just the validator: the tool
        // must fail rather than hand back an image nothing can open.
        var harness = HarnessAnnouncing(CaptureUri);
        harness.Downloader.Body = ImageFixtures.MarkerCorruptedJpeg;

        Assert.Equal(TvErrorCode.TvError, await CodeOf(harness));
    }

    [Fact]
    public void The_good_fixtures_still_pass_the_structural_walk()
    {
        // The other half of the mutation argument: the checks must reject corrupt
        // images WITHOUT rejecting real ones. A validator that failed everything
        // would pass every negative control above.
        Assert.Equal("image/jpeg", ScreenshotPolicy.DetectImageMimeType(ImageFixtures.Jpeg));
        Assert.Equal("image/png", ScreenshotPolicy.DetectImageMimeType(ImageFixtures.Png));
    }

    [Fact]
    public void Trailing_bytes_after_the_terminator_are_rejected_for_both_formats()
    {
        // An image with something appended is not the image it claims to be.
        Assert.Throws<TvException>(
            () => ScreenshotPolicy.DetectImageMimeType([.. ImageFixtures.Jpeg, .. "trailing"u8]));

        Assert.Throws<TvException>(
            () => ScreenshotPolicy.DetectImageMimeType([.. ImageFixtures.Png, .. "trailing"u8]));
    }

    [Fact]
    public void A_jpeg_padded_with_trailing_nulls_after_its_EOI_is_still_accepted()
    {
        // Some transports pad to a block boundary. A bounded allowance accepts that
        // without letting an arbitrary tail stand in for a missing terminator.
        var padded = ImageFixtures.Jpeg.Concat(new byte[8]).ToArray();

        Assert.Equal("image/jpeg", ScreenshotPolicy.DetectImageMimeType(padded));

        // Past the allowance the terminator is no longer at the end, and the body is
        // treated as truncated rather than padded.
        var overPadded = ImageFixtures.Jpeg.Concat(new byte[64]).ToArray();

        Assert.Throws<TvException>(() => ScreenshotPolicy.DetectImageMimeType(overPadded));
    }

    // ------------------------------------------------------ the real downloader

    private static ScreenshotDownloader Downloader(
        HttpMessageHandler handler,
        Action<WebosMcpOptions>? configure = null)
    {
        var options = new WebosMcpOptions { Host = TvHost, ScreenshotTimeoutSeconds = 5 };
        configure?.Invoke(options);

        return new ScreenshotDownloader(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ScreenshotDownloader>.Instance);
    }

    private static async Task<TvErrorCode> DownloadCodeOf(
        HttpMessageHandler handler,
        Action<WebosMcpOptions>? configure = null)
    {
        var ex = await Assert.ThrowsAsync<TvException>(
            () => Downloader(handler, configure).DownloadAsync(new Uri(CaptureUri), CancellationToken.None));
        return ex.Code;
    }

    [Fact]
    public async Task A_same_host_redirect_is_followed()
    {
        var handler = new ScriptedHttpHandler()
            .Redirect(HttpStatusCode.Found, $"http://{TvHost}:9080/tmp/real.jpg")
            .Image(ImageFixtures.Jpeg);

        var bytes = await Downloader(handler).DownloadAsync(new Uri(CaptureUri), CancellationToken.None);

        Assert.Equal(ImageFixtures.Jpeg, bytes.ToArray());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_relative_redirect_resolves_against_the_pinned_host()
    {
        var handler = new ScriptedHttpHandler()
            .Redirect(HttpStatusCode.Found, "/tmp/real.jpg")
            .Image(ImageFixtures.Jpeg);

        await Downloader(handler).DownloadAsync(new Uri(CaptureUri), CancellationToken.None);

        Assert.Equal($"http://{TvHost}:9080/tmp/real.jpg", handler.Requests[1]);
    }

    [Fact]
    public async Task A_cross_host_redirect_is_refused_as_INVALID_INPUT_and_never_followed()
    {
        var handler = new ScriptedHttpHandler()
            .Redirect(HttpStatusCode.Found, "https://example.invalid/capture.jpg")
            .Image(ImageFixtures.Jpeg);

        Assert.Equal(TvErrorCode.InvalidInput, await DownloadCodeOf(handler));

        // Only the first hop was ever requested. Following it and then complaining
        // would already have leaked the request off the TV.
        Assert.Equal([CaptureUri], handler.Requests);
    }

    [Theory]
    [InlineData("ftp://192.0.2.10/capture.jpg")]
    [InlineData("file:///tmp/capture.jpg")]
    [InlineData("ws://192.0.2.10/capture.jpg")]
    public async Task A_redirect_to_a_disallowed_scheme_is_INVALID_INPUT(string location)
    {
        // The blocker this closes: a first hop that passes every check can redirect
        // to a target that passes none of them. Host pinning alone would have let
        // file:// through, because the host matches.
        var handler = new ScriptedHttpHandler()
            .Redirect(HttpStatusCode.Found, location)
            .Image(ImageFixtures.Jpeg);

        Assert.Equal(TvErrorCode.InvalidInput, await DownloadCodeOf(handler));
        Assert.Equal([CaptureUri], handler.Requests);
    }

    [Fact]
    public async Task A_redirect_carrying_userinfo_is_INVALID_INPUT()
    {
        // Same host, valid scheme, embedded credentials. Only the full policy
        // catches this, and the credentials would otherwise be sent by HttpClient.
        var handler = new ScriptedHttpHandler()
            .Redirect(HttpStatusCode.Found, $"http://user:pass@{TvHost}:9080/tmp/real.jpg")
            .Image(ImageFixtures.Jpeg);

        Assert.Equal(TvErrorCode.InvalidInput, await DownloadCodeOf(handler));
        Assert.Equal([CaptureUri], handler.Requests);
    }

    [Fact]
    public async Task A_redirect_to_an_over_long_target_is_INVALID_INPUT()
    {
        var handler = new ScriptedHttpHandler()
            .Redirect(HttpStatusCode.Found, $"http://{TvHost}:9080/{new string('a', 4096)}.jpg")
            .Image(ImageFixtures.Jpeg);

        Assert.Equal(TvErrorCode.InvalidInput, await DownloadCodeOf(handler));
        Assert.Equal([CaptureUri], handler.Requests);
    }

    [Fact]
    public async Task A_redirect_loop_is_bounded_and_reported_as_TV_ERROR()
    {
        var handler = new ScriptedHttpHandler().AlwaysRedirect($"http://{TvHost}:9080/again.jpg");

        Assert.Equal(TvErrorCode.TvError, await DownloadCodeOf(handler));
        Assert.Equal(ScreenshotPolicy.MaxRedirects + 1, handler.Requests.Count);
    }

    [Fact]
    public async Task A_redirect_with_no_location_is_TV_ERROR()
    {
        var handler = new ScriptedHttpHandler().Status(HttpStatusCode.Found);

        Assert.Equal(TvErrorCode.TvError, await DownloadCodeOf(handler));
    }

    [Fact]
    public async Task A_non_success_status_is_TV_ERROR()
    {
        var handler = new ScriptedHttpHandler().Status(HttpStatusCode.NotFound);

        Assert.Equal(TvErrorCode.TvError, await DownloadCodeOf(handler));
    }

    [Fact]
    public async Task A_download_that_never_answers_is_TIMEOUT()
    {
        var handler = new ScriptedHttpHandler().Hang();

        Assert.Equal(
            TvErrorCode.Timeout,
            await DownloadCodeOf(handler, options => options.ScreenshotTimeoutSeconds = 1));
    }

    [Fact]
    public async Task A_body_declaring_an_oversized_length_is_TV_ERROR()
    {
        var handler = new ScriptedHttpHandler().Image(new byte[4096]);

        Assert.Equal(
            TvErrorCode.TvError,
            await DownloadCodeOf(handler, options => options.ScreenshotMaxBytes = WebosMcpOptions.MinScreenshotMaxBytes));
    }

    [Fact]
    public async Task A_body_that_declares_no_length_is_still_capped_while_streaming()
    {
        // The realistic oversize case: a chunked response cannot be pre-checked, so
        // the cap has to hold while the bytes are being read.
        var handler = new ScriptedHttpHandler().StreamedImage(new byte[4096]);

        Assert.Equal(
            TvErrorCode.TvError,
            await DownloadCodeOf(handler, options => options.ScreenshotMaxBytes = WebosMcpOptions.MinScreenshotMaxBytes));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, WebosMcpOptions.DefaultScreenshotMaxBytes)]
    [InlineData(WebosMcpOptions.DefaultScreenshotTimeoutSeconds, 0)]
    public async Task The_downloader_reads_the_validated_limits_not_the_raw_ones(int seconds, int maxBytes)
    {
        // Without this, the range checks could all pass while the download still
        // used the unchecked field — the property would be validated and unread.
        var handler = new ScriptedHttpHandler().Image(ImageFixtures.Jpeg);

        var ex = await Assert.ThrowsAsync<TvException>(
            () => Downloader(handler, options =>
            {
                options.ScreenshotTimeoutSeconds = seconds;
                options.ScreenshotMaxBytes = maxBytes;
            }).DownloadAsync(new Uri(CaptureUri), CancellationToken.None));

        Assert.Equal(TvErrorCode.InvalidInput, ex.Code);

        // Refused before the request, so a bad limit cannot half-run a capture.
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_caller_cancellation_is_reported_as_cancellation_not_as_a_tv_timeout()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Downloader(new ScriptedHttpHandler().Hang())
                .DownloadAsync(new Uri(CaptureUri), cts.Token));
    }

    // ------------------------------------------------------- configured limits

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(301)]
    [InlineData(int.MaxValue)]
    public void An_out_of_range_screenshot_timeout_is_rejected_not_clamped(int seconds)
    {
        // 0 and -1 are the dangerous ones: CancelAfter with a non-positive delay
        // does not mean "no timeout" in the way an operator might expect, and a
        // negative value throws from deep inside the download instead of being
        // reported as the configuration error it is.
        var options = new WebosMcpOptions { ScreenshotTimeoutSeconds = seconds };

        var ex = Assert.Throws<TvException>(() => options.ResolvedScreenshotTimeoutSeconds);

        Assert.Equal(TvErrorCode.InvalidInput, ex.Code);
        Assert.Contains("WEBOSMCP__SCREENSHOTTIMEOUTSECONDS", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1023)]
    [InlineData(int.MaxValue)]
    public void An_out_of_range_screenshot_size_cap_is_rejected_not_clamped(int bytes)
    {
        // A cap of 0 or a negative would make every capture fail; int.MaxValue would
        // remove the memory bound the cap exists to provide.
        var options = new WebosMcpOptions { ScreenshotMaxBytes = bytes };

        var ex = Assert.Throws<TvException>(() => options.ResolvedScreenshotMaxBytes);

        Assert.Equal(TvErrorCode.InvalidInput, ex.Code);
        Assert.Contains("WEBOSMCP__SCREENSHOTMAXBYTES", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_accepted_range_boundaries_are_usable()
    {
        // The rejection tests above would also pass if everything were rejected.
        Assert.Equal(
            WebosMcpOptions.MinScreenshotTimeoutSeconds,
            new WebosMcpOptions
            {
                ScreenshotTimeoutSeconds = WebosMcpOptions.MinScreenshotTimeoutSeconds,
            }.ResolvedScreenshotTimeoutSeconds);

        Assert.Equal(
            WebosMcpOptions.MaxScreenshotTimeoutSeconds,
            new WebosMcpOptions
            {
                ScreenshotTimeoutSeconds = WebosMcpOptions.MaxScreenshotTimeoutSeconds,
            }.ResolvedScreenshotTimeoutSeconds);

        Assert.Equal(
            WebosMcpOptions.MinScreenshotMaxBytes,
            new WebosMcpOptions
            {
                ScreenshotMaxBytes = WebosMcpOptions.MinScreenshotMaxBytes,
            }.ResolvedScreenshotMaxBytes);

        Assert.Equal(
            WebosMcpOptions.MaxScreenshotMaxBytes,
            new WebosMcpOptions
            {
                ScreenshotMaxBytes = WebosMcpOptions.MaxScreenshotMaxBytes,
            }.ResolvedScreenshotMaxBytes);

        Assert.Null(new WebosMcpOptions().ValidateScreenshotLimits());
    }

    [Fact]
    public void The_startup_validator_names_the_offending_setting()
    {
        // A generic "configuration is invalid" would leave an operator guessing
        // which of two settings is wrong and what range it should be in.
        var validator = new ScreenshotLimitsValidator();

        var result = validator.Validate(
            null, new WebosMcpOptions { ScreenshotMaxBytes = 0 });

        Assert.True(result.Failed);
        Assert.Contains("WEBOSMCP__SCREENSHOTMAXBYTES", result.FailureMessage!, StringComparison.Ordinal);

        Assert.True(validator.Validate(null, new WebosMcpOptions()).Succeeded);
    }

    // ------------------------------------------------------------- no leakage

    [Fact]
    public async Task Nothing_about_the_capture_reaches_a_log_line()
    {
        var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(capture);
        });

        var harness = HarnessAnnouncing(CaptureUri, factory);
        var shot = await harness.Control.CaptureScreenshotAsync(CancellationToken.None);

        var lines = string.Join("\n", capture.Lines);

        Assert.DoesNotContain(CaptureUri, lines, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/capture.jpg", lines, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(shot.Bytes.Span), lines, StringComparison.Ordinal);
        Assert.DoesNotContain("test-client-key", lines, StringComparison.Ordinal);

        // The size and format ARE logged: an operator has to be able to tell a
        // capture happened without the frame ever being written anywhere.
        Assert.Contains(lines, char.IsDigit);
    }

    [Fact]
    public async Task A_rejected_imageUri_is_not_echoed_into_the_error_message()
    {
        // The message reaches a caller, and the URI is TV-supplied. Naming the rule
        // is actionable; echoing the value is not worth the exposure.
        var harness = HarnessAnnouncing("https://example.invalid/secret-path/capture.jpg");

        var ex = await Assert.ThrowsAsync<TvException>(
            () => harness.Control.CaptureScreenshotAsync(CancellationToken.None));

        Assert.DoesNotContain("example.invalid", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-path", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------ the content block

    [Fact]
    public void An_image_block_carries_base64_TEXT_not_the_raw_bytes()
    {
        // ImageContentBlock.Data is the base64 TEXT as UTF-8 bytes, not the image.
        // Assigning raw bytes compiles, serialises to a well-formed "type":"image"
        // block, and produces a data field no client can decode. This asserts the
        // encoding rather than merely that a block was produced.
        var bytes = ImageFixtures.Jpeg;

        var block = ToolContent.Image(bytes, "image/jpeg");

        Assert.Equal("image/jpeg", block.MimeType);
        Assert.Equal(Convert.ToBase64String(bytes), Encoding.UTF8.GetString(block.Data.Span));
        Assert.Equal(bytes, block.DecodedData.ToArray());
        Assert.NotEqual(bytes, block.Data.ToArray());
    }

    [Fact]
    public async Task The_tool_returns_an_image_block_on_success()
    {
        var harness = HarnessAnnouncing(CaptureUri);
        var tools = new ScreenshotTools(
            harness.Control, harness.LoggerFactory.CreateLogger<ScreenshotTools>());

        var result = await tools.TakeScreenshot(CancellationToken.None);

        var block = Assert.IsType<ImageContentBlock>(Assert.Single(result.Content));
        Assert.Equal("image/jpeg", block.MimeType);
        Assert.Equal(ImageFixtures.Jpeg, block.DecodedData.ToArray());
    }

    [Fact]
    public async Task The_tool_falls_back_to_the_shared_error_envelope_on_failure()
    {
        var connection = new FakeSsapConnection();
        connection.Fail(SsapUri.ExecuteOneShot, TvException.Unsupported("frame capture"));
        var harness = new TestHarness(connection);

        var tools = new ScreenshotTools(
            harness.Control, harness.LoggerFactory.CreateLogger<ScreenshotTools>());

        var result = await tools.TakeScreenshot(CancellationToken.None);

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        using var payload = System.Text.Json.JsonDocument.Parse(text);

        Assert.False(payload.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "TV_UNSUPPORTED_CAPABILITY",
            payload.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}

/// <summary>
/// Scriptable HTTP responses for the download path. A queue rather than a single
/// response, because redirect handling is the part most worth testing and it needs
/// more than one hop.
/// </summary>
public sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();
    private Func<HttpResponseMessage>? _always;
    private bool _hang;

    public List<string> Requests { get; } = [];

    public ScriptedHttpHandler Redirect(HttpStatusCode status, string location)
    {
        _responses.Enqueue(() =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            return response;
        });

        return this;
    }

    public ScriptedHttpHandler AlwaysRedirect(string location)
    {
        _always = () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            return response;
        };

        return this;
    }

    public ScriptedHttpHandler Status(HttpStatusCode status)
    {
        _responses.Enqueue(() => new HttpResponseMessage(status));
        return this;
    }

    public ScriptedHttpHandler Image(byte[] body)
    {
        _responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        });

        return this;
    }

    /// <summary>A body with no Content-Length, as a chunked response would arrive.</summary>
    public ScriptedHttpHandler StreamedImage(byte[] body)
    {
        _responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new UnmeasurableStream(body)),
        });

        return this;
    }

    public ScriptedHttpHandler Hang()
    {
        _hang = true;
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!.AbsoluteUri);

        if (_hang)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }

        if (_responses.Count > 0)
        {
            return _responses.Dequeue()();
        }

        return (_always ?? (() => new HttpResponseMessage(HttpStatusCode.NotFound)))();
    }
}

/// <summary>Non-seekable so HttpClient cannot derive a Content-Length from it.</summary>
internal sealed class UnmeasurableStream(byte[] body) : Stream
{
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = body.Length - _position;
        if (remaining <= 0)
        {
            return 0;
        }

        // Deliberately dribbles the body out so the cap is exercised mid-stream
        // rather than on a single read.
        var take = Math.Min(Math.Min(count, remaining), 512);
        Array.Copy(body, _position, buffer, offset, take);
        _position += take;
        return take;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

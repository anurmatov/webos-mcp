using System.Net;
using WebosMcp.Domain;

namespace WebosMcp.Application;

/// <summary>
/// One captured frame. Held in memory for the duration of the request and
/// nothing more: it is never written to the device store, a temp file, a cache,
/// a log line or any telemetry payload.
/// </summary>
public sealed record CapturedScreenshot(ReadOnlyMemory<byte> Bytes, string MimeType);

/// <summary>
/// The safety rules for a capture, kept pure so every branch is exercised with
/// no TV and no HTTP server.
///
/// The <c>imageUri</c> is supplied BY THE TV, so it is untrusted input in exactly
/// the sense the rest of this project treats caller input as untrusted. The TV is
/// the thing being controlled, not an authority on where this process should send
/// a request — pinning the download to the selected TV's own host is what stops a
/// compromised or spoofed response turning the server into a fetch primitive.
/// </summary>
public static class ScreenshotPolicy
{
    /// <summary>
    /// Redirect hops allowed. Every hop is re-pinned to the selected TV, so this
    /// bounds a same-host redirect loop rather than permitting a wander.
    /// </summary>
    public const int MaxRedirects = 3;

    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The complete terminal PNG chunk: zero-length payload, "IEND", and its fixed CRC.</summary>
    private static readonly byte[] PngIend =
        [0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];

    /// <summary>
    /// Smallest plausible complete file per format. A baseline JPEG carries full
    /// quantisation and Huffman tables whatever the image size, so even a 2x2 is
    /// several hundred bytes; a PNG needs signature, IHDR, an IDAT and IEND.
    /// </summary>
    private const int MinJpegBytes = 128;

    private const int MinPngBytes = 57;

    /// <summary>
    /// Trailing NUL bytes tolerated after a JPEG's EOI marker. Some transports pad
    /// to a block boundary; a bounded allowance accepts that without letting an
    /// arbitrary tail stand in for a missing terminator.
    /// </summary>
    private const int MaxTrailingPadding = 16;

    /// <summary>
    /// Validates the URI the TV announced. Every rejection here is
    /// <see cref="TvErrorCode.InvalidInput"/>: the request was refused before
    /// trusting TV-supplied data that failed the same safety contract caller
    /// input has to pass.
    /// </summary>
    public static Uri ValidateImageUri(string? imageUri, WebosMcpOptions options)
    {
        if (string.IsNullOrWhiteSpace(imageUri))
        {
            throw TvException.Invalid(
                "The TV accepted the capture request but its response carried no imageUri, so there is nothing to download.");
        }

        if (imageUri.Length > InputValidation.MaxUrlLength)
        {
            throw TvException.Invalid(
                $"The TV's imageUri exceeds the maximum length of {InputValidation.MaxUrlLength} characters.");
        }

        if (!Uri.TryCreate(imageUri.Trim(), UriKind.Absolute, out var uri))
        {
            throw TvException.Invalid("The TV's imageUri is not a well-formed absolute URL.");
        }

        ValidateTarget(uri, options, "The TV's imageUri");

        return uri;
    }

    /// <summary>
    /// The complete rule set for anything this download will actually request.
    ///
    /// Applied to the announced <c>imageUri</c> AND to every redirect target. That
    /// symmetry is the point: a redirect is just a second URI supplied by the same
    /// untrusted source, so checking only the host on later hops would leave a
    /// <c>file://</c> target or embedded credentials reachable one redirect away
    /// from a URI that passed every check.
    /// </summary>
    public static void ValidateTarget(Uri uri, WebosMcpOptions options, string what)
    {
        if (uri.AbsoluteUri.Length > InputValidation.MaxUrlLength)
        {
            throw TvException.Invalid(
                $"{what} exceeds the maximum length of {InputValidation.MaxUrlLength} characters.");
        }

        // Deliberately NOT InputValidation.ValidateHttpsUrl: that is HTTPS-only by
        // design for tv_open_url, where the target is an arbitrary external site.
        // This target is the TV itself on the local network, which commonly serves
        // the capture over plain HTTP with no TLS at all. The guarantee here is a
        // pinned host, not a scheme.
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw TvException.Invalid(
                $"{what} uses the '{uri.Scheme}' scheme; only http and https are accepted.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw TvException.Invalid($"{what} carries userinfo credentials, which are not accepted.");
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            throw TvException.Invalid($"{what} has no host component.");
        }

        if (!IsSelectedTvHost(uri, options))
        {
            // The host itself is not echoed: it is TV-supplied and this message
            // reaches a caller. Naming the rule is enough to act on.
            throw TvException.Invalid(
                $"{what} would leave the selected TV's host, which is refused. " +
                "Only the TV that produced the capture may serve it.");
        }
    }

    /// <summary>
    /// True when <paramref name="uri"/> addresses the currently selected TV.
    ///
    /// The configured host may be a name while the TV announces an address, so the
    /// name's resolved addresses count as the same host. Resolution failure is not
    /// an error here — it simply yields no extra alias, and the comparison fails
    /// closed.
    /// </summary>
    public static bool IsSelectedTvHost(Uri? uri, WebosMcpOptions options)
    {
        if (uri is null || string.IsNullOrWhiteSpace(options.Host))
        {
            return false;
        }

        var candidate = Unbracket(uri.Host);
        var configured = options.Host!.Trim();

        if (HostsMatch(candidate, configured))
        {
            return true;
        }

        if (IPAddress.TryParse(configured, out _))
        {
            return false;
        }

        try
        {
            foreach (var address in Dns.GetHostAddresses(configured))
            {
                if (HostsMatch(candidate, address.ToString()))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Identifies the payload by its bytes, and requires the file to be COMPLETE.
    ///
    /// The <c>Content-Type</c> header is not consulted: a TV — or anything
    /// answering in its place — can label an HTML error page <c>image/jpeg</c>.
    ///
    /// Nor is a leading magic number sufficient. A download cut short by a reset
    /// connection keeps its signature and loses its tail, so a prefix check calls a
    /// truncated, undecodable body a successful capture — the same unverified
    /// success this project refuses everywhere else, arriving through the one door
    /// a header check does not cover. Each format is therefore accepted only with
    /// its terminator present: a JPEG's EOI marker, a PNG's IEND chunk.
    ///
    /// WebP is deliberately NOT supported. A RIFF length field can be made
    /// self-consistent over arbitrary content, so the check available for it is
    /// materially weaker than the other two, and the verified probe returned JPEG.
    /// A TV that answers with WebP gets an honest TV_ERROR naming the supported
    /// formats rather than a capture validated to a lower standard.
    /// </summary>
    public static string DetectImageMimeType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            throw new TvException(
                TvErrorCode.TvError,
                "The capture download returned an empty body, so no image was captured.");
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            if (bytes.Length < MinJpegBytes || !EndsWithJpegEoi(bytes))
            {
                throw Incomplete("JPEG", "its EOI (FF D9) end-of-image marker", bytes.Length);
            }

            return Jpeg;
        }

        if (bytes.Length >= PngSignature.Length && bytes[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            if (bytes.Length < MinPngBytes || !bytes[^PngIend.Length..].SequenceEqual(PngIend))
            {
                throw Incomplete("PNG", "its terminal IEND chunk", bytes.Length);
            }

            return Png;
        }

        throw new TvException(
            TvErrorCode.TvError,
            $"The capture download returned {bytes.Length} bytes that are not a supported image. " +
            "Expected a complete JPEG or PNG, identified by content rather than by the declared Content-Type.");
    }

    private static TvException Incomplete(string format, string terminator, int length) => new(
        TvErrorCode.TvError,
        $"The capture download returned {length} bytes that begin as a {format} but are missing " +
        $"{terminator}. The download was truncated, so the image is incomplete and is not reported " +
        "as a successful capture.");

    private static bool EndsWithJpegEoi(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.Length;
        var floor = Math.Max(2, end - MaxTrailingPadding);

        while (end > floor && bytes[end - 1] == 0x00)
        {
            end--;
        }

        return end >= 2 && bytes[end - 2] == 0xFF && bytes[end - 1] == 0xD9;
    }

    private static bool HostsMatch(string candidate, string other)
    {
        var right = Unbracket(other);

        if (IPAddress.TryParse(candidate, out var left) && IPAddress.TryParse(right, out var parsed))
        {
            return left.Equals(parsed);
        }

        return string.Equals(candidate, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><see cref="Uri.Host"/> brackets an IPv6 literal; an IP string does not.</summary>
    private static string Unbracket(string host)
    {
        var value = host.Trim();
        return value.StartsWith('[') && value.EndsWith(']') ? value[1..^1] : value;
    }
}

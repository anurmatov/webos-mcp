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
    public const string WebP = "image/webp";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

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

        // Deliberately NOT InputValidation.ValidateHttpsUrl: that is HTTPS-only by
        // design for tv_open_url, where the target is an arbitrary external site.
        // This target is the TV itself on the local network, which commonly serves
        // the capture over plain HTTP with no TLS at all. The guarantee here is a
        // pinned host, not a scheme.
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw TvException.Invalid(
                $"The TV's imageUri uses the '{uri.Scheme}' scheme; only http and https are accepted.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw TvException.Invalid("The TV's imageUri carries userinfo credentials, which are not accepted.");
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            throw TvException.Invalid("The TV's imageUri has no host component.");
        }

        RequireSelectedTvHost(uri, options);

        return uri;
    }

    /// <summary>
    /// Pins a request target to the selected TV. Called for the first request AND
    /// for every redirect hop — a redirect that leaves the TV is the same class of
    /// failure as an imageUri that never pointed at it, and is reported as such.
    /// </summary>
    public static void RequireSelectedTvHost(Uri uri, WebosMcpOptions options)
    {
        if (!IsSelectedTvHost(uri, options))
        {
            // The host itself is not echoed: it is TV-supplied and this message
            // reaches a caller. Naming the rule is enough to act on.
            throw TvException.Invalid(
                "The capture download would leave the selected TV's host, which is refused. " +
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
    /// Identifies the payload by its bytes. The <c>Content-Type</c> header is NOT
    /// consulted: a TV — or anything answering in its place — can label an HTML
    /// error page <c>image/jpeg</c>, and reporting that as a successful capture
    /// would be exactly the kind of unverified success this project refuses
    /// everywhere else.
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
            return Jpeg;
        }

        if (bytes.Length >= PngSignature.Length && bytes[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            return Png;
        }

        if (bytes.Length >= 12 &&
            bytes[..4].SequenceEqual("RIFF"u8) &&
            bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return WebP;
        }

        throw new TvException(
            TvErrorCode.TvError,
            $"The capture download returned {bytes.Length} bytes that are not a supported image. " +
            "Expected JPEG, PNG or WebP, identified by content rather than by the declared Content-Type.");
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

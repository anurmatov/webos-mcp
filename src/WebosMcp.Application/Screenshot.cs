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
    /// Identifies the payload by its bytes, and requires the file to be
    /// STRUCTURALLY WELL FORMED — not merely to start and end correctly.
    ///
    /// Three checks were tried and each was weaker than it looked:
    ///
    ///   - The <c>Content-Type</c> header. Anything answering in the TV's place can
    ///     label an HTML error page <c>image/jpeg</c>.
    ///   - A leading magic number. A download cut short keeps its signature and
    ///     loses its tail, so a truncated, unopenable body reads as a success.
    ///   - A signature AND a terminator. This is the subtle one: a body can begin
    ///     with SOI, end with EOI, and be corrupt in between — a mangled segment
    ///     length, a chunk whose contents no longer match its checksum — and still
    ///     pass. Bracketing bytes say nothing about what is between them.
    ///
    /// So the bytes are walked: every JPEG marker segment must carry a length that
    /// fits, with a frame header and a scan present and the scan running to EOI;
    /// every PNG chunk must carry a length that fits AND a CRC32 that matches its
    /// own contents, ending at IEND with nothing after it. That is what makes
    /// "this is an image" a checked claim rather than a plausible one.
    ///
    /// It is deliberately NOT a pixel decode — no production dependency is worth
    /// that here, and a decoder is a large attack surface to point at untrusted
    /// bytes. The known gap is stated rather than papered over: corruption inside
    /// a JPEG's entropy-coded scan is invisible to any structural check, because
    /// that region is arbitrary bytes by definition. PNG has no such gap, since
    /// every byte of it is inside a CRC-covered chunk.
    ///
    /// WebP is deliberately NOT supported. A RIFF length field can be made
    /// self-consistent over arbitrary content, and it carries no per-chunk
    /// checksum, so it cannot be held to this standard. The verified probe returns
    /// JPEG; a TV answering with WebP gets an honest TV_ERROR.
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
            if (bytes.Length < MinJpegBytes)
            {
                throw Malformed("JPEG", "it is far too short to hold a frame header and a scan", bytes.Length);
            }

            if (ValidateJpeg(TrimTrailingPadding(bytes)) is { } jpegProblem)
            {
                throw Malformed("JPEG", jpegProblem, bytes.Length);
            }

            return Jpeg;
        }

        if (bytes.Length >= PngSignature.Length && bytes[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            if (bytes.Length < MinPngBytes)
            {
                throw Malformed("PNG", "it is far too short to hold IHDR, IDAT and IEND", bytes.Length);
            }

            if (ValidatePng(bytes) is { } pngProblem)
            {
                throw Malformed("PNG", pngProblem, bytes.Length);
            }

            return Png;
        }

        throw new TvException(
            TvErrorCode.TvError,
            $"The capture download returned {bytes.Length} bytes that are not a supported image. " +
            "Expected a complete JPEG or PNG, identified by content rather than by the declared Content-Type.");
    }

    private static TvException Malformed(string format, string problem, int length) => new(
        TvErrorCode.TvError,
        $"The capture download returned {length} bytes that begin as a {format} but are not a valid one: " +
        $"{problem}. A truncated or corrupt body is not reported as a successful capture.");

    /// <summary>
    /// Walks the marker structure. Returns null when well formed, or a short
    /// description of the first problem found.
    /// </summary>
    private static string? ValidateJpeg(ReadOnlySpan<byte> bytes)
    {
        var i = 2; // past SOI
        var seenFrameHeader = false;
        var seenScan = false;

        while (i < bytes.Length)
        {
            if (bytes[i] != 0xFF)
            {
                return $"expected a marker at byte {i} and found none";
            }

            // Any number of 0xFF fill bytes may precede a marker.
            while (i < bytes.Length && bytes[i] == 0xFF)
            {
                i++;
            }

            if (i >= bytes.Length)
            {
                return "the data ends in marker padding with no end-of-image marker";
            }

            var marker = bytes[i++];

            // Standalone markers carry no segment.
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                continue;
            }

            if (marker == 0xD9) // EOI
            {
                if (i != bytes.Length)
                {
                    return $"{bytes.Length - i} unexpected bytes follow the end-of-image marker";
                }

                if (!seenFrameHeader)
                {
                    return "it carries no frame header (SOF), so it describes no image";
                }

                return seenScan ? null : "it carries no scan (SOS), so it contains no image data";
            }

            if (i + 2 > bytes.Length)
            {
                return $"the segment at byte {i} is cut off before its length";
            }

            var segmentLength = (bytes[i] << 8) | bytes[i + 1];

            // The length includes its own two bytes, so anything below 2 is
            // nonsense and anything overrunning the buffer is corrupt.
            if (segmentLength < 2 || i + segmentLength > bytes.Length)
            {
                return $"the segment at byte {i} declares a length of {segmentLength} that does not fit";
            }

            // SOF0..SOF15, excluding DHT (C4), JPG (C8) and DAC (CC).
            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                seenFrameHeader = true;
            }

            i += segmentLength;

            if (marker == 0xDA) // SOS: entropy-coded data follows, then a marker
            {
                seenScan = true;
                i = SkipEntropyCodedData(bytes, i);
            }
        }

        return "the data ends without an end-of-image marker";
    }

    /// <summary>
    /// Advances past the entropy-coded scan to the next real marker.
    ///
    /// Inside the scan a literal 0xFF is stuffed as <c>FF 00</c>, and restart
    /// markers are expected — neither ends the scan. Anything else after an 0xFF
    /// does.
    /// </summary>
    private static int SkipEntropyCodedData(ReadOnlySpan<byte> bytes, int i)
    {
        while (i < bytes.Length)
        {
            if (bytes[i] != 0xFF)
            {
                i++;
                continue;
            }

            if (i + 1 >= bytes.Length)
            {
                return i;
            }

            var next = bytes[i + 1];

            if (next == 0x00 || (next >= 0xD0 && next <= 0xD7))
            {
                i += 2;
                continue;
            }

            if (next == 0xFF)
            {
                i++;
                continue;
            }

            return i;
        }

        return i;
    }

    /// <summary>
    /// Walks the chunk structure and verifies every CRC. Returns null when well
    /// formed, or a short description of the first problem found.
    ///
    /// The CRC is what makes this stronger than a bracket check: every byte of a
    /// PNG lives inside a chunk that carries a checksum over its own contents, so
    /// corruption anywhere is detectable without decoding a single pixel.
    /// </summary>
    private static string? ValidatePng(ReadOnlySpan<byte> bytes)
    {
        var i = PngSignature.Length;
        var first = true;
        var seenHeader = false;
        var seenData = false;

        while (i < bytes.Length)
        {
            if (i + 8 > bytes.Length)
            {
                return $"the chunk at byte {i} is cut off before its header";
            }

            var length = ((uint)bytes[i] << 24) | ((uint)bytes[i + 1] << 16) |
                         ((uint)bytes[i + 2] << 8) | bytes[i + 3];

            if (length > int.MaxValue)
            {
                return $"the chunk at byte {i} declares an impossible length";
            }

            var typeStart = i + 4;
            var dataStart = typeStart + 4;

            if (dataStart + (long)length + 4 > bytes.Length)
            {
                return $"the chunk at byte {i} declares a length of {length} that does not fit";
            }

            var type = bytes.Slice(typeStart, 4);
            var declaredCrc = ((uint)bytes[dataStart + (int)length] << 24) |
                              ((uint)bytes[dataStart + (int)length + 1] << 16) |
                              ((uint)bytes[dataStart + (int)length + 2] << 8) |
                              bytes[dataStart + (int)length + 3];

            // The CRC covers the type and the data, not the length.
            if (Crc32(bytes.Slice(typeStart, 4 + (int)length)) != declaredCrc)
            {
                return $"the {Describe(type)} chunk at byte {i} does not match its own CRC32 checksum";
            }

            var isHeader = type.SequenceEqual("IHDR"u8);
            var isEnd = type.SequenceEqual("IEND"u8);

            if (first && !isHeader)
            {
                return "its first chunk is not IHDR";
            }

            seenHeader |= isHeader;
            seenData |= type.SequenceEqual("IDAT"u8);
            first = false;

            i = dataStart + (int)length + 4;

            if (isEnd)
            {
                if (i != bytes.Length)
                {
                    return $"{bytes.Length - i} unexpected bytes follow the IEND chunk";
                }

                if (!seenHeader)
                {
                    return "it carries no IHDR chunk, so it describes no image";
                }

                return seenData ? null : "it carries no IDAT chunk, so it contains no image data";
            }
        }

        return "the data ends without an IEND chunk";
    }

    private static string Describe(ReadOnlySpan<byte> type)
    {
        Span<char> name = stackalloc char[4];
        for (var i = 0; i < 4; i++)
        {
            // Chunk types are ASCII letters; anything else is shown as '?' rather
            // than echoed raw into a message.
            name[i] = char.IsAsciiLetter((char)type[i]) ? (char)type[i] : '?';
        }

        return new string(name);
    }

    /// <summary>
    /// The standard CRC-32 PNG uses. Implemented here rather than taken from a
    /// package: it is fifteen lines, and a production dependency for it would be a
    /// poor trade.
    /// </summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;

        foreach (var b in data)
        {
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];

        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    /// <summary>
    /// Drops a bounded run of trailing NULs. Some transports pad to a block
    /// boundary, and that padding is not part of the JPEG.
    /// </summary>
    private static ReadOnlySpan<byte> TrimTrailingPadding(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.Length;
        var floor = Math.Max(2, end - MaxTrailingPadding);

        while (end > floor && bytes[end - 1] == 0x00)
        {
            end--;
        }

        return bytes[..end];
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

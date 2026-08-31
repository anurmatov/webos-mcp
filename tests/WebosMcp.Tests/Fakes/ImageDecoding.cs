using System.IO.Compression;

namespace WebosMcp.Tests.Fakes;

/// <summary>
/// Test-only image readers.
///
/// These exist so a success assertion can be "the bytes that came back over the
/// wire really are an image of the expected size" rather than "the bytes equal the
/// fixture I put in". A byte-compare proves the transport copied an array; it
/// proves nothing about whether the array was ever a decodable image, so it would
/// pass just as happily if the fixture were garbage.
///
/// Deliberately test-only and dependency-free. Nothing here runs in production —
/// pointing a decoder at untrusted bytes is a large attack surface, and the server
/// validates structure instead.
/// </summary>
internal static class ImageDecoding
{
    public sealed record DecodedImage(int Width, int Height, byte[] Rgba);

    /// <summary>
    /// A genuine PNG decode: chunk walk, zlib inflate, and scanline unfiltering
    /// through to real pixels. Supports exactly what the fixture is — 8-bit RGBA,
    /// non-interlaced — and throws on anything else rather than guessing.
    /// </summary>
    public static DecodedImage DecodePng(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(signature))
        {
            throw new InvalidDataException("Not a PNG: bad signature.");
        }

        int width = 0, height = 0;
        var compressed = new MemoryStream();
        var sawHeader = false;
        var i = 8;

        while (i + 8 <= bytes.Length)
        {
            var length = (bytes[i] << 24) | (bytes[i + 1] << 16) | (bytes[i + 2] << 8) | bytes[i + 3];
            var type = System.Text.Encoding.ASCII.GetString(bytes, i + 4, 4);
            var dataStart = i + 8;

            if (dataStart + length + 4 > bytes.Length)
            {
                throw new InvalidDataException($"Chunk '{type}' runs past the end of the file.");
            }

            switch (type)
            {
                case "IHDR":
                    width = (bytes[dataStart] << 24) | (bytes[dataStart + 1] << 16) |
                            (bytes[dataStart + 2] << 8) | bytes[dataStart + 3];
                    height = (bytes[dataStart + 4] << 24) | (bytes[dataStart + 5] << 16) |
                             (bytes[dataStart + 6] << 8) | bytes[dataStart + 7];

                    var bitDepth = bytes[dataStart + 8];
                    var colourType = bytes[dataStart + 9];
                    var interlace = bytes[dataStart + 12];

                    if (bitDepth != 8 || colourType != 6 || interlace != 0)
                    {
                        throw new InvalidDataException(
                            $"Unsupported PNG for this decoder: depth {bitDepth}, colour type {colourType}, " +
                            $"interlace {interlace}. Expected 8-bit RGBA, non-interlaced.");
                    }

                    sawHeader = true;
                    break;

                case "IDAT":
                    compressed.Write(bytes, dataStart, length);
                    break;
            }

            i = dataStart + length + 4;

            if (type == "IEND")
            {
                break;
            }
        }

        if (!sawHeader)
        {
            throw new InvalidDataException("PNG has no IHDR chunk.");
        }

        compressed.Position = 0;
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress))
        {
            zlib.CopyTo(inflated);
        }

        return new DecodedImage(width, height, Unfilter(inflated.ToArray(), width, height));
    }

    /// <summary>Reverses the per-scanline filters defined by the PNG spec.</summary>
    private static byte[] Unfilter(byte[] raw, int width, int height)
    {
        const int bytesPerPixel = 4;
        var stride = width * bytesPerPixel;
        var expected = height * (stride + 1);

        if (raw.Length != expected)
        {
            throw new InvalidDataException(
                $"Inflated PNG data is {raw.Length} bytes; expected {expected} for {width}x{height} RGBA.");
        }

        var output = new byte[height * stride];

        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var rowStart = (y * (stride + 1)) + 1;

            for (var x = 0; x < stride; x++)
            {
                int left = x >= bytesPerPixel ? output[(y * stride) + x - bytesPerPixel] : 0;
                int up = y > 0 ? output[((y - 1) * stride) + x] : 0;
                int upLeft = y > 0 && x >= bytesPerPixel ? output[((y - 1) * stride) + x - bytesPerPixel] : 0;

                var value = raw[rowStart + x];

                output[(y * stride) + x] = filter switch
                {
                    0 => value,
                    1 => (byte)(value + left),
                    2 => (byte)(value + up),
                    3 => (byte)(value + ((left + up) / 2)),
                    4 => (byte)(value + Paeth(left, up, upLeft)),
                    _ => throw new InvalidDataException($"Unknown PNG filter type {filter} on row {y}."),
                };
            }
        }

        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);

        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>
    /// Reads a JPEG's real dimensions out of its frame header, walking the marker
    /// segments to find it.
    ///
    /// Honest about what it is: this reads the frame header, it does not decode
    /// pixels. A baseline JPEG decoder is Huffman decoding plus an inverse DCT, and
    /// hand-rolling one to assert two integers would be more likely to be wrong
    /// than the thing it checks. Reaching a well-formed SOF at the right offset
    /// still requires every preceding segment to be intact, which is what the
    /// corrupt-middle fixture breaks.
    /// </summary>
    public static (int Width, int Height) ReadJpegDimensions(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
        {
            throw new InvalidDataException("Not a JPEG: bad SOI marker.");
        }

        var i = 2;

        while (i < bytes.Length)
        {
            if (bytes[i] != 0xFF)
            {
                throw new InvalidDataException($"Expected a JPEG marker at byte {i}.");
            }

            while (i < bytes.Length && bytes[i] == 0xFF)
            {
                i++;
            }

            if (i >= bytes.Length)
            {
                throw new InvalidDataException("JPEG ends in marker padding.");
            }

            var marker = bytes[i++];

            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                continue;
            }

            if (marker == 0xD9)
            {
                throw new InvalidDataException("JPEG ended before a frame header was found.");
            }

            if (i + 2 > bytes.Length)
            {
                throw new InvalidDataException($"JPEG segment at byte {i} is cut off.");
            }

            var segmentLength = (bytes[i] << 8) | bytes[i + 1];

            if (segmentLength < 2 || i + segmentLength > bytes.Length)
            {
                throw new InvalidDataException($"JPEG segment at byte {i} has an impossible length.");
            }

            // SOF0/1/2 carry height and width immediately after the precision byte.
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                var height = (bytes[i + 3] << 8) | bytes[i + 4];
                var width = (bytes[i + 5] << 8) | bytes[i + 6];
                return (width, height);
            }

            i += segmentLength;

            if (marker == 0xDA)
            {
                throw new InvalidDataException("JPEG scan began before any frame header.");
            }
        }

        throw new InvalidDataException("JPEG contains no frame header.");
    }
}

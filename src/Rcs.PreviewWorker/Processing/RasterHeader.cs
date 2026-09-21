using System.Buffers.Binary;

namespace Rcs.PreviewWorker.Processing;

/// <summary>The pixel size a raster file declares.</summary>
public sealed record RasterSize(int Width, int Height)
{
    public long Pixels => (long)Width * Height;
}

/// <summary>
/// Reads the declared dimensions of a PNG, JPEG or WebP from its header, so an oversized image is refused BEFORE a
/// decoder allocates memory for it. Bounded: it reads at most <see cref="MaxScanBytes"/> bytes and never decodes pixels.
/// </summary>
public static class RasterHeader
{
    public const int MaxScanBytes = 1024 * 1024;

    public static RasterSize? Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        var length = (int)Math.Min(stream.Length, MaxScanBytes);
        var buffer = new byte[length];
        stream.ReadExactly(buffer);
        return Read(buffer);
    }

    public static RasterSize? Read(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 24 && data[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])
            && data[12..16].SequenceEqual("IHDR"u8))
        {
            return Valid(BinaryPrimitives.ReadInt32BigEndian(data[16..20]), BinaryPrimitives.ReadInt32BigEndian(data[20..24]));
        }

        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            return ReadJpeg(data);
        }

        if (data.Length >= 30 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
        {
            return ReadWebp(data);
        }

        return null;
    }

    private static RasterSize? ReadJpeg(ReadOnlySpan<byte> data)
    {
        var index = 2;
        while (index + 4 <= data.Length)
        {
            if (data[index] != 0xFF)
            {
                return null;
            }

            var marker = data[index + 1];
            if (marker == 0xFF)
            {
                index++;
                continue;
            }

            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                index += 2;
                continue;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data[(index + 2)..]);
            if (segmentLength < 2)
            {
                return null;
            }

            // SOF0..SOF15 except DHT (C4), JPG (C8) and DAC (CC) carry the frame size.
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (index + 9 > data.Length)
                {
                    return null;
                }

                var height = BinaryPrimitives.ReadUInt16BigEndian(data[(index + 5)..]);
                var width = BinaryPrimitives.ReadUInt16BigEndian(data[(index + 7)..]);
                return Valid(width, height);
            }

            if (marker == 0xDA)
            {
                return null; // Start of scan before a frame header: malformed.
            }

            index += 2 + segmentLength;
        }

        return null;
    }

    private static RasterSize? ReadWebp(ReadOnlySpan<byte> data)
    {
        var chunk = data[12..16];
        if (chunk.SequenceEqual("VP8X"u8))
        {
            var width = 1 + (data[24] | (data[25] << 8) | (data[26] << 16));
            var height = 1 + (data[27] | (data[28] << 8) | (data[29] << 16));
            return Valid(width, height);
        }

        if (chunk.SequenceEqual("VP8L"u8) && data[20] == 0x2F)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[21..25]);
            return Valid((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }

        if (chunk.SequenceEqual("VP8 "u8) && data[23] == 0x9D && data[24] == 0x01 && data[25] == 0x2A)
        {
            return Valid(BinaryPrimitives.ReadUInt16LittleEndian(data[26..28]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(data[28..30]) & 0x3FFF);
        }

        return null;
    }

    private static RasterSize? Valid(int width, int height) => width > 0 && height > 0 ? new RasterSize(width, height) : null;
}

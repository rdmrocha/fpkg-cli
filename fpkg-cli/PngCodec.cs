using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Fpkg.Cli;

/// <summary>
/// The PNG half of the sce_sys media repair: structural validation of an existing file, and
/// encoding of a replacement.
///
/// Both exist because LibProsperoPkg cannot do either off Windows. It never validates a PNG at
/// all — through 0.6.7 the media loop reads a present file with File.ReadAllBytes and packs it
/// unchecked, so a corrupt icon0.png ships as-is. And its one repair path,
/// ProsperoDdsEncoder.DecodeDdsToPng, decodes the DDS with BCnEncoder (managed) but encodes the
/// PNG with Magick.NET, whose native half ships only as runtimes/win-x64/native. Encoding a PNG
/// is a zlib stream and three chunks, so it is done here instead and the whole path stays
/// managed.
///
/// Kept in its own file, like PatchStamp, so the tests can link it without pulling in the CLI's
/// assembly-resolving module initializer.
/// </summary>
internal static class PngCodec
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Full structural validation, not a signature sniff: the 8-byte signature, IHDR first, then
    /// the whole chunk chain with every declared length and CRC-32 checked, ending at IEND with
    /// nothing after it.
    ///
    /// Each of those catches a failure mode seen in real dumps: no signature at all (one dump's
    /// pic2.png is 532 bytes of high-entropy data), a chunk that runs past EOF (truncation), and a
    /// CRC mismatch (bit-rot). The cost is one pass over a file that is about to be
    /// Kraken-compressed anyway.
    /// </summary>
    internal static bool IsValid(ReadOnlySpan<byte> data, out string reason)
    {
        if (data.Length < Signature.Length + 12)
        {
            reason = $"{data.Length} bytes, too short for a PNG";
            return false;
        }
        if (!data[..Signature.Length].SequenceEqual(Signature))
        {
            reason = "no PNG signature";
            return false;
        }

        int offset = Signature.Length;
        bool first = true, sawEnd = false;
        while (offset + 8 <= data.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
            // Guard the cast before it is used for anything: a hostile or corrupt length must not
            // be allowed to overflow the offset arithmetic below.
            if (length > int.MaxValue - 12)
            {
                reason = $"chunk at 0x{offset:X} declares {length} bytes";
                return false;
            }
            long end = (long)offset + 12 + length;   // 4 length + 4 type + payload + 4 CRC
            if (end > data.Length)
            {
                reason = $"truncated: chunk at 0x{offset:X} needs {end:N0} bytes, file is {data.Length:N0}";
                return false;
            }
            var type = data.Slice(offset + 4, 4);
            if (first && !type.SequenceEqual("IHDR"u8))
            {
                reason = "first chunk is not IHDR";
                return false;
            }
            uint stored = BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 8 + (int)length)..]);
            if (stored != Crc32(data.Slice(offset + 4, 4 + (int)length)))
            {
                reason = $"CRC mismatch in {Encoding.ASCII.GetString(type)} at 0x{offset:X}";
                return false;
            }
            if (type.SequenceEqual("IEND"u8)) { sawEnd = true; offset = (int)end; break; }
            first = false;
            offset = (int)end;
        }
        if (!sawEnd)
        {
            reason = "no IEND chunk";
            return false;
        }
        if (offset != data.Length)
        {
            reason = $"{data.Length - offset:N0} trailing byte(s) after IEND";
            return false;
        }
        reason = "";
        return true;
    }

    internal static bool IsValid(string path, out string reason)
    {
        try { return IsValid(File.ReadAllBytes(path), out reason); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Writes 8-bit RGB or RGBA as a non-interlaced PNG. <paramref name="pixels"/> is tightly
    /// packed, <paramref name="width"/> * (alpha ? 4 : 3) bytes per row.
    ///
    /// Every scanline uses filter type 0 (None). Filtering would compress better, but this runs
    /// once per damaged image and the result is Kraken-packed immediately afterwards, so the
    /// simpler encoder is the right trade.
    /// </summary>
    internal static byte[] Encode(int width, int height, ReadOnlySpan<byte> pixels, bool alpha)
    {
        int channels = alpha ? 4 : 3;
        int stride = checked(width * channels);
        if (pixels.Length != checked(stride * height))
            throw new ArgumentException(
                $"expected {stride * height} pixel bytes for {width}x{height}, got {pixels.Length}", nameof(pixels));

        var raw = new byte[checked((stride + 1) * height)];
        for (int y = 0; y < height; y++)
        {
            raw[y * (stride + 1)] = 0;                                   // filter: None
            pixels.Slice(y * stride, stride).CopyTo(raw.AsSpan(y * (stride + 1) + 1));
        }

        using var deflated = new MemoryStream();
        using (var z = new ZLibStream(deflated, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw, 0, raw.Length);

        using var png = new MemoryStream();
        png.Write(Signature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8;                          // bit depth
        ihdr[9] = (byte)(alpha ? 6 : 2);      // colour type: RGBA / RGB
        ihdr[10] = ihdr[11] = ihdr[12] = 0;   // deflate, adaptive filtering, no interlace
        WriteChunk(png, "IHDR"u8, ihdr);
        WriteChunk(png, "IDAT"u8, deflated.ToArray());
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        Span<byte> scratch = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(scratch, (uint)payload.Length);
        output.Write(scratch);
        // Type and payload are CRC'd together, so they are laid out contiguously once.
        var body = new byte[4 + payload.Length];
        type.CopyTo(body);
        payload.CopyTo(body.AsSpan(4));
        output.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(scratch, Crc32(body));
        output.Write(scratch);
    }

    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}

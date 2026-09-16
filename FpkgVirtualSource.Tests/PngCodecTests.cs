using System.Buffers.Binary;
using Fpkg.Cli;
using Xunit;

namespace FpkgVirtualSource.Tests;

/// <summary>
/// Pins <see cref="PngCodec"/>, which is what stands between a dump's damaged sce_sys image and a
/// package that ships it. LibProsperoPkg validates nothing here — through 0.6.7 a present PNG is
/// read with File.ReadAllBytes and packed as-is — so every one of these cases is a defect that
/// would otherwise reach a console.
///
/// The corruption shapes are taken from a real dump: its pic2.png is 532 bytes of high-entropy
/// data with no PNG signature, sitting next to a valid pic2.dds.
/// </summary>
public class PngCodecTests
{
    /// <summary>A 4x2 image, encoded by the same code the repair path uses.</summary>
    private static byte[] ValidPng(bool alpha = false)
    {
        int channels = alpha ? 4 : 3;
        var pixels = new byte[4 * 2 * channels];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 7);
        return PngCodec.Encode(4, 2, pixels, alpha);
    }

    [Fact]
    public void RoundTripsItsOwnOutput()
    {
        Assert.True(PngCodec.IsValid(ValidPng(), out var reason), reason);
        Assert.True(PngCodec.IsValid(ValidPng(alpha: true), out reason), reason);
    }

    [Fact]
    public void EncodesTheHeaderTheDecoderSideExpects()
    {
        var png = ValidPng(alpha: true);
        // IHDR payload starts at 8 (signature) + 4 (length) + 4 (type).
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20)));
        Assert.Equal(8, png[24]);   // bit depth
        Assert.Equal(6, png[25]);   // colour type RGBA
        Assert.Equal(0, png[26]);   // deflate
        Assert.Equal(0, png[27]);   // adaptive filtering
        Assert.Equal(0, png[28]);   // no interlace
        Assert.Equal(2, ValidPng(alpha: false)[25]);   // colour type RGB
    }

    [Fact]
    public void RejectsHighEntropyDataWithNoSignature()
    {
        // The observed pic2.png case, reduced: 532 bytes that are not a PNG at all.
        var junk = new byte[532];
        new Random(1).NextBytes(junk);
        Assert.False(PngCodec.IsValid(junk, out var reason));
        Assert.Equal("no PNG signature", reason);
    }

    [Fact]
    public void RejectsTruncation()
    {
        var png = ValidPng();
        Assert.False(PngCodec.IsValid(png.AsSpan(0, png.Length - 20), out var reason));
        Assert.Contains("truncated", reason);
    }

    [Fact]
    public void RejectsAFlippedBitInThePayload()
    {
        var png = ValidPng();
        // Land inside IDAT, past the IHDR chunk (8 signature + 25 IHDR = 33 bytes).
        png[40] ^= 0xFF;
        Assert.False(PngCodec.IsValid(png, out var reason));
        Assert.Contains("CRC mismatch", reason);
    }

    [Fact]
    public void RejectsTrailingBytesAfterIend()
    {
        var png = ValidPng();
        Assert.False(PngCodec.IsValid([.. png, 0x00], out var reason));
        Assert.Contains("trailing byte", reason);
    }

    [Fact]
    public void RejectsAFileThatIsTooShortToHoldAChunk()
    {
        Assert.False(PngCodec.IsValid(new byte[8], out var reason));
        Assert.Contains("too short", reason);
    }

    [Fact]
    public void RejectsAChunkLengthThatWouldOverflowTheOffsetArithmetic()
    {
        var png = ValidPng();
        // Rewrite the IHDR length to something that cannot be added to the offset safely. The
        // guard must reject it rather than wrap into a negative index.
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8), uint.MaxValue);
        Assert.False(PngCodec.IsValid(png, out var reason));
        Assert.Contains("declares", reason);
    }

    [Fact]
    public void RejectsAFirstChunkThatIsNotIhdr()
    {
        var png = ValidPng();
        "IDAT"u8.CopyTo(png.AsSpan(12));
        // Re-CRC so the type check is what fails, not the checksum.
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(25), PngCodec.Crc32(png.AsSpan(12, 17)));
        Assert.False(PngCodec.IsValid(png, out var reason));
        Assert.Equal("first chunk is not IHDR", reason);
    }

    [Fact]
    public void RejectsAStreamWithNoIend()
    {
        var png = ValidPng();
        "IDAU"u8.CopyTo(png.AsSpan(png.Length - 8));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(png.Length - 4), PngCodec.Crc32(png.AsSpan(png.Length - 8, 4)));
        Assert.False(PngCodec.IsValid(png, out var reason));
        Assert.Equal("no IEND chunk", reason);
    }

    [Fact]
    public void Crc32MatchesTheKnownPngIendValue()
    {
        // Every PNG ends with the same IEND chunk, so its CRC is a fixed, externally verifiable
        // constant: 0xAE426082.
        Assert.Equal(0xAE426082u, PngCodec.Crc32("IEND"u8));
    }

    [Fact]
    public void EncodeRejectsAPixelBufferThatDoesNotMatchTheDimensions()
    {
        Assert.Throws<ArgumentException>(() => PngCodec.Encode(4, 2, new byte[10], alpha: false));
    }
}

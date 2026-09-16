using System.Security.Cryptography;
using PprPfsKrakenTool;
using Xunit;

public class OodleBackendTests
{
    private static byte[] Corpus(int n, int kind)
    {
        var b = new byte[n];
        switch (kind)
        {
            case 0: break;                                   // all zero
            case 1: RandomNumberGenerator.Fill(b); break;     // incompressible
            case 2:                                           // compressible
                for (int i = 0; i < n; i++) b[i] = (byte)(i % 1024 < 700 ? (i >> 7) : i * 31);
                break;
            case 3:                                           // real binary
                var src = File.ReadAllBytes(ReleaseFolder.Find() + "/LibProsperoPkg.dll");
                for (int i = 0; i < n; i++) b[i] = src[i % src.Length];
                break;
        }
        return b;
    }

    public static IEnumerable<object[]> Cases()
    {
        int[] sizes = { 1, 4095, 65536, 131071, 131072, 131073, 262143, 262144 };
        int[] levels = { -4, 1, 7, 9 };
        foreach (var s in sizes) foreach (var l in levels) foreach (var k in new[] { 0, 1, 2, 3 })
            yield return new object[] { s, l, k };
    }

    [SkippableTheory]
    [MemberData(nameof(Cases))]
    public void EveryBlockEitherVerifiesOrIsRejected(int size, int level, int kind)
    {
        Skip.IfNot(OodleBackend.IsAvailable(null), OodleBackend.Describe(null));

        byte[] data = Corpus(size, kind);
        bool ok = OodleBackend.TryEncodeBlock(data, level,
            out var payload, out var multiChunk, out var firstChunkCompSize, out var flags);

        if (!ok) return;   // rejected -> caller stores the block raw; that is a valid outcome

        Assert.NotNull(payload);
        Assert.InRange(payload.Length, 1, data.Length);
        Assert.Equal(size > 131072, multiChunk);
        if (multiChunk) Assert.InRange(firstChunkCompSize, 1, payload.Length - 1);
        else Assert.Equal(0, firstChunkCompSize);

        // The library's own decoder is the oracle: an independent reimplementation,
        // so agreement is evidence rather than tautology.
        Assert.True(OodleBackend.VerifyWithLibraryDecoder(payload, flags, firstChunkCompSize, data),
            $"KrakenDecoder rejected size={size} level={level} kind={kind} flags=0x{flags:X}");
    }

    /// <summary>
    /// Guards against the theory above going vacuous: EveryBlockEitherVerifiesOrIsRejected
    /// treats "rejected" as a valid outcome (`if (!ok) return;`), so a regression that made
    /// TryEncodeBlock bail on every input would still pass all 128 theory cases. This fact
    /// asserts a floor on genuinely compressible input: encoding must actually succeed, and
    /// the multi-chunk path (Ruling D's dependent-chunk-1 case) must actually be exercised.
    /// Self-contained: no shared static state, no dependency on theory execution order.
    /// </summary>
    [SkippableFact]
    public void CompressibleBlocksActuallyEncode_NotJustBail()
    {
        Skip.IfNot(OodleBackend.IsAvailable(null), OodleBackend.Describe(null));

        int[] sizes = { 65536, 131072, 131073, 262144 };
        int[] levels = { 1, 7 };
        int[] kinds = { 0, 2, 3 };   // zeros, synthetic-compressible, real binary

        bool sawVerifiedMultiChunk = false;

        foreach (var size in sizes)
        foreach (var level in levels)
        foreach (var kind in kinds)
        {
            byte[] data = Corpus(size, kind);
            bool ok = OodleBackend.TryEncodeBlock(data, level,
                out var payload, out var multiChunk, out var firstChunkCompSize, out var flags);

            Assert.True(ok,
                $"expected TryEncodeBlock to succeed on compressible input: size={size} level={level} kind={kind}");

            Assert.True(OodleBackend.VerifyWithLibraryDecoder(payload, flags, firstChunkCompSize, data),
                $"KrakenDecoder rejected size={size} level={level} kind={kind} flags=0x{flags:X}");

            if (size == 262144 && multiChunk && firstChunkCompSize > 0)
                sawVerifiedMultiChunk = true;
        }

        Assert.True(sawVerifiedMultiChunk,
            "expected at least one verified multi-chunk (262144-byte) encode with firstChunkCompSize > 0");
    }

    /// <summary>
    /// Describe() has two outputs: the encoder description when a RAD Oodle library resolves, and
    /// a "looked for …" diagnostic when none does. Only the first is asserted here, so this has to
    /// skip like every other test in this class when no library is present — otherwise the suite
    /// is red on any machine that has not been given one, which is the normal state of the repo.
    /// </summary>
    [SkippableFact]
    public void DescribeNamesTheEncoderAndItsLimits()
    {
        string d = OodleBackend.Describe(null);
        Skip.If(d.StartsWith("no RAD Oodle library found", StringComparison.Ordinal),
                "no RAD Oodle library in fpkg-tools/native/ or native/");
        Assert.Contains("RAD", d);
        Assert.Contains("not Sony publisher-identical", d);
    }
}

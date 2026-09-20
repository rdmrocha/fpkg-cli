using System.Buffers.Binary;
using System.Text;

namespace Fpkg.Cli.PlayGo;

/// <summary>
/// A reader and writer for <c>playgo-chunk.dat</c> (the <c>plgx</c> container), written here rather
/// than taken from the library because <c>ProsperoPlayGo.BuildMultiChunkDat</c> can only express
/// Sony's exact shape — one chunk-0 main extent, one extent per language chunk, one chunk-0 tail —
/// and a layout derived from measurement splits chunk 0 into however many runs the file order
/// actually produces. That is the whole point of the fix, so the emitter has to be able to say it.
///
/// <para>
/// Everything is little-endian; this is the PlayGo descriptor format, not the big-endian CNT
/// header. The field map is <c>docs/HANDOFF-windows-parity.md</c> §7b.
/// </para>
///
/// <para>
/// The writer deliberately carries the ORIGINAL 256-byte header and the original 32-byte chunk
/// records through verbatim, patching only the fields it is entitled to change. Several header
/// words (0x18, and whatever 0x1C/0x1E really are — §7b's <c>sdk_version</c> u32 and
/// <c>layer_bmp</c> u16 overlap, so at most one of the two spellings can be right) have no
/// established meaning. Re-deriving them from scratch would mean inventing values; copying them
/// means <see cref="RoundTrips"/> can be a genuine byte-equality test rather than a test of the
/// subset this code happens to understand.
/// </para>
/// </summary>
internal sealed class Plgx
{
    internal const ulong ValueMask = 0x0000_FFFF_FFFF_FFFFul;   // 48 bits, PROVEN (spec §2)
    private const int HeaderSize = 256;
    private const int SectionDescriptors = 0xC0;
    private const int Alignment = 16;

    // Header field offsets, §7b.
    private const int NChunks = 0x0A;
    private const int NScenarios = 0x0E;
    private const int FileSize = 0x10;
    private const int MchunkCount = 0x20;
    private const int DefaultLanguage = 0x24;
    private const int LanguageMask = 0x38;

    internal sealed class Chunk
    {
        /// <summary>The record verbatim; every field this class does not name survives a rebuild.</summary>
        internal byte[] Raw = new byte[32];
        internal ulong Mask;                       // +0x10
        internal List<uint> ExtentRefs = [];
        internal string Label = "";
    }

    internal sealed class Scenario
    {
        internal byte[] Raw = new byte[32];
        internal int InitialChunks;                // +0x14
        internal List<ushort> ChunkRefs = [];
        internal string Label = "";
    }

    internal readonly record struct Extent(ulong Offset, ulong Length);

    internal byte[] Header = new byte[HeaderSize];
    internal List<Chunk> Chunks = [];
    internal List<Extent> Extents = [];
    internal List<Scenario> Scenarios = [];

    internal ulong SupportedLanguageMask
    {
        get => BinaryPrimitives.ReadUInt64LittleEndian(Header.AsSpan(LanguageMask));
        set => BinaryPrimitives.WriteUInt64LittleEndian(Header.AsSpan(LanguageMask), value);
    }

    internal byte DefaultLanguageId
    {
        get => Header[DefaultLanguage];
        set => Header[DefaultLanguage] = value;
    }

    internal static Plgx Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < HeaderSize || !d[..4].SequenceEqual("plgx"u8))
            throw new InvalidDataException(
                $"playgo-chunk.dat is not a plgx container ({d.Length} bytes, magic " +
                $"'{Encoding.ASCII.GetString(d[..Math.Min(4, d.Length)])}').");
        if (BinaryPrimitives.ReadUInt32LittleEndian(d[FileSize..]) != d.Length)
            throw new InvalidDataException(
                $"plgx file_size ({BinaryPrimitives.ReadUInt32LittleEndian(d[FileSize..])}) " +
                $"disagrees with the payload length ({d.Length}).");

        var p = new Plgx();
        d[..HeaderSize].CopyTo(p.Header);

        var sec = new (int Offset, int Size)[8];
        for (int i = 0; i < sec.Length; i++)
            sec[i] = ((int)BinaryPrimitives.ReadUInt32LittleEndian(d[(SectionDescriptors + i * 8)..]),
                      (int)BinaryPrimitives.ReadUInt32LittleEndian(d[(SectionDescriptors + i * 8 + 4)..]));

        int nChunks = BinaryPrimitives.ReadUInt16LittleEndian(d[NChunks..]);
        int nExtents = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[MchunkCount..]);
        int nScenarios = BinaryPrimitives.ReadUInt16LittleEndian(d[NScenarios..]);

        for (int i = 0; i < nChunks; i++)
        {
            var r = d.Slice(sec[0].Offset + i * 32, 32);
            var c = new Chunk { Mask = BinaryPrimitives.ReadUInt64LittleEndian(r[0x10..]) };
            r.CopyTo(c.Raw);
            int refCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(r[4..]);
            int refAt = (int)BinaryPrimitives.ReadUInt32LittleEndian(r[0x18..]);
            for (int k = 0; k < refCount; k++)
                c.ExtentRefs.Add(BinaryPrimitives.ReadUInt32LittleEndian(d[(sec[1].Offset + refAt + k * 4)..]));
            c.Label = NulTerminated(d, sec[2].Offset + (int)BinaryPrimitives.ReadUInt32LittleEndian(r[0x1C..]));
            p.Chunks.Add(c);
        }

        for (int i = 0; i < nExtents; i++)
        {
            var r = d.Slice(sec[3].Offset + i * 16, 16);
            p.Extents.Add(new Extent(BinaryPrimitives.ReadUInt64LittleEndian(r) & ValueMask,
                                     BinaryPrimitives.ReadUInt64LittleEndian(r[8..]) & ValueMask));
        }

        for (int i = 0; i < nScenarios; i++)
        {
            var r = d.Slice(sec[4].Offset + i * 32, 32);
            var s = new Scenario { InitialChunks = BinaryPrimitives.ReadUInt16LittleEndian(r[0x14..]) };
            r.CopyTo(s.Raw);
            int refCount = BinaryPrimitives.ReadUInt16LittleEndian(r[0x16..]);
            int refAt = (int)BinaryPrimitives.ReadUInt32LittleEndian(r[0x18..]);
            for (int k = 0; k < refCount; k++)
                s.ChunkRefs.Add(BinaryPrimitives.ReadUInt16LittleEndian(d[(sec[5].Offset + refAt + k * 2)..]));
            s.Label = NulTerminated(d, sec[6].Offset + (int)BinaryPrimitives.ReadUInt32LittleEndian(r[0x1C..]));
            p.Scenarios.Add(s);
        }

        return p;
    }

    /// <summary>
    /// Emits the container. Section order and the 16-byte alignment between sections are MEASURED
    /// from <c>the Windows-built reference package</c>'s own <c>playgo-chunk.dat</c>, and <see cref="RoundTrips"/>
    /// is what keeps that honest: it re-emits a parsed file and requires byte equality, so a wrong
    /// order or a wrong pad shows up as a failure rather than as a package that merely verifies.
    /// </summary>
    internal byte[] Build()
    {
        var chunkAttrs = new byte[Chunks.Count * 32];
        var extentRefs = new List<byte>();
        var chunkLabels = new List<byte>();
        var labelAt = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < Chunks.Count; i++)
        {
            var c = Chunks[i];
            var r = chunkAttrs.AsSpan(i * 32, 32);
            c.Raw.CopyTo(r);
            BinaryPrimitives.WriteUInt32LittleEndian(r[4..], (uint)c.ExtentRefs.Count);
            BinaryPrimitives.WriteUInt64LittleEndian(r[0x10..], c.Mask);
            // An empty list still records the current end of the section, which is what the oracle
            // does for its 68 unused chunks: they all point one past the last real reference.
            BinaryPrimitives.WriteUInt32LittleEndian(r[0x18..], (uint)extentRefs.Count);
            foreach (uint e in c.ExtentRefs)
            {
                var tmp = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(tmp, e);
                extentRefs.AddRange(tmp);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(r[0x1C..], (uint)Intern(c.Label, chunkLabels, labelAt));
        }

        var extents = new byte[Extents.Count * 16];
        for (int i = 0; i < Extents.Count; i++)
        {
            var r = extents.AsSpan(i * 16, 16);
            BinaryPrimitives.WriteUInt64LittleEndian(r, Extents[i].Offset & ValueMask);
            BinaryPrimitives.WriteUInt64LittleEndian(r[8..], Extents[i].Length & ValueMask);
        }

        var scenarioAttrs = new byte[Scenarios.Count * 32];
        var scenarioRefs = new List<byte>();
        var scenarioLabels = new List<byte>();
        var scenarioLabelAt = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < Scenarios.Count; i++)
        {
            var s = Scenarios[i];
            var r = scenarioAttrs.AsSpan(i * 32, 32);
            s.Raw.CopyTo(r);
            BinaryPrimitives.WriteUInt16LittleEndian(r[0x14..], (ushort)s.InitialChunks);
            BinaryPrimitives.WriteUInt16LittleEndian(r[0x16..], (ushort)s.ChunkRefs.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(r[0x18..], (uint)scenarioRefs.Count);
            foreach (ushort c in s.ChunkRefs)
            {
                var tmp = new byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(tmp, c);
                scenarioRefs.AddRange(tmp);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(r[0x1C..], (uint)Intern(s.Label, scenarioLabels, scenarioLabelAt));
        }

        byte[][] sections =
        [
            chunkAttrs, [.. extentRefs], [.. chunkLabels], extents,
            scenarioAttrs, [.. scenarioRefs], [.. scenarioLabels],
        ];

        var offsets = new int[sections.Length];
        int at = HeaderSize;
        for (int i = 0; i < sections.Length; i++)
        {
            at = Align(at);
            offsets[i] = at;
            at += sections[i].Length;
        }
        int total = Align(at);

        var outBytes = new byte[total];
        Header.CopyTo(outBytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(outBytes.AsSpan(NChunks), (ushort)Chunks.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(outBytes.AsSpan(NScenarios), (ushort)Scenarios.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(FileSize), (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(MchunkCount), (uint)Extents.Count);
        for (int i = 0; i < sections.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(SectionDescriptors + i * 8), (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(SectionDescriptors + i * 8 + 4), (uint)sections[i].Length);
            sections[i].CopyTo(outBytes, offsets[i]);
        }
        // Descriptor 7 (0xF8) is plgo-only; §7b says leave it zero, and the header copy already did.
        return outBytes;
    }

    /// <summary>
    /// The writer's own gate: parse <paramref name="original"/> and re-emit it. Returns null on
    /// byte equality, or the first difference. Nothing else in this feature may be trusted until
    /// this passes on a real package's <c>playgo-chunk.dat</c>.
    /// </summary>
    internal static string? RoundTrips(byte[] original)
    {
        byte[] again = Parse(original).Build();
        if (again.Length != original.Length)
            return $"re-emitting playgo-chunk.dat changes its length ({original.Length} -> {again.Length}).";
        for (int i = 0; i < original.Length; i++)
            if (original[i] != again[i])
                return $"re-emitting playgo-chunk.dat differs at offset 0x{i:X} " +
                       $"(original 0x{original[i]:X2}, re-emitted 0x{again[i]:X2}).";
        return null;
    }

    /// <summary>
    /// Labels are pooled by text, which is not a space optimisation: the oracle's 68 unused chunks
    /// each carry their own distinct label, so pooling only ever collapses genuinely repeated
    /// strings and the round-trip test is what proves it collapses none of the oracle's.
    /// </summary>
    private static int Intern(string label, List<byte> pool, Dictionary<string, int> seen)
    {
        if (seen.TryGetValue(label, out int at)) return at;
        at = pool.Count;
        pool.AddRange(Encoding.ASCII.GetBytes(label));
        pool.Add(0);
        seen[label] = at;
        return at;
    }

    private static int Align(int value) => (value + Alignment - 1) / Alignment * Alignment;

    private static string NulTerminated(ReadOnlySpan<byte> d, int at)
    {
        int end = at;
        while (end < d.Length && d[end] != 0) end++;
        return Encoding.ASCII.GetString(d[at..end]);
    }
}

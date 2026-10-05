using System.Buffers.Binary;
using System.IO.Compression;
using Fpkg.Cli;
using Xunit;

/// <summary>
/// The LIH merge on a miniature base + patch built here, laid out the way Publishing Tools lays
/// out a real one. The real-size check (byte parity with <c>img_convert</c> on a 34 GB title) is
/// out of reach of a unit test; this pins the rule and every refusal.
/// </summary>
public class LihMergeTests : IDisposable
{
    private const int BS = LihMerge.BlockSize;
    private readonly string _dir = Directory.CreateTempSubdirectory("lihmerge").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static readonly byte[] BaseDigest = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    /// <summary>A block whose every byte says where it came from, so a misplaced copy shows.</summary>
    private static byte[] Block(byte tag, int index)
    {
        var b = new byte[BS];
        for (int i = 0; i < BS; i += 4) { b[i] = tag; BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i + 1), (ushort)index); }
        return b;
    }

    private string BuildBase(int blocks, byte[]? digest = null)
    {
        var fih = Block(0xB0, 0);
        "\u007fFIH"u8.CopyTo(fih);
        (digest ?? BaseDigest).CopyTo(fih, 0x30);
        string path = Path.Combine(_dir, "base.pkg");
        using var f = File.Create(path);
        f.Write(fih);
        for (int i = 1; i < blocks; i++) f.Write(Block(0xBA, i));
        f.Write(new byte[1234]);   // a ragged tail, like a real SI zip end
        return path;
    }

    /// <summary>
    /// LIH header | target FIH | patch data blocks | SC | SI zip. <paramref name="map"/> is the
    /// relocinfo, one entry per target block. Returns the patch path and the expected output.
    /// </summary>
    private (string Patch, byte[] Expected) BuildPatch(uint[] map, int dataBlocks, string basePath,
                                                       Action<byte[]>? tamperLih = null, long? cntOffset = null)
    {
        var fih = Block(0xF0, 0);
        "\u007fFIH"u8.CopyTo(fih);
        BinaryPrimitives.WriteUInt64LittleEndian(fih.AsSpan(0x58), (ulong)(cntOffset ?? (long)map.Length * BS));
        BaseDigest.CopyTo(fih, 0xD0);
        var data = Enumerable.Range(0, dataBlocks).Select(i => Block(0xDA, i)).ToList();
        var sc = new byte[BS * 2 + 77];
        Array.Fill(sc, (byte)0x5C);

        var rlc = new byte[0x1000 + map.Length * 4];
        "\u007fRLC"u8.CopyTo(rlc);
        BinaryPrimitives.WriteUInt32LittleEndian(rlc.AsSpan(0x14), (uint)map.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(rlc.AsSpan(0x20), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(rlc.AsSpan(0x24), (uint)map.Length * 4);
        for (int i = 0; i < map.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(rlc.AsSpan(0x1000 + i * 4), map[i]);
        var siStream = new MemoryStream();
        using (var zip = new ZipArchive(siStream, ZipArchiveMode.Create, leaveOpen: true))
        using (var e = zip.CreateEntry(LihMerge.RelocEntry, CompressionLevel.NoCompression).Open())
            e.Write(rlc);
        var si = siStream.ToArray();

        long scOff = (long)(2 + dataBlocks) * BS, siOff = scOff + sc.Length;
        var lih = new byte[BS];
        "\u007fLIH"u8.CopyTo(lih);
        BinaryPrimitives.WriteUInt64LittleEndian(lih.AsSpan(0x18), (ulong)(siOff + si.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(lih.AsSpan(0x20), BS);
        BinaryPrimitives.WriteUInt64LittleEndian(lih.AsSpan(0x30), (ulong)scOff);
        BinaryPrimitives.WriteUInt64LittleEndian(lih.AsSpan(0x38), (ulong)sc.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(lih.AsSpan(0x40), (ulong)siOff);
        BinaryPrimitives.WriteUInt64LittleEndian(lih.AsSpan(0x48), (ulong)si.Length);
        tamperLih?.Invoke(lih);

        byte[][] patchBlocks = [lih, fih, .. data];
        string path = Path.Combine(_dir, "patch.pkg");
        File.WriteAllBytes(path, [.. patchBlocks.SelectMany(b => b), .. sc, .. si]);

        var baseBytes = File.ReadAllBytes(basePath);
        var expected = new List<byte>();
        foreach (var v in map)
        {
            int n = (int)(v & 0x0FFF_FFFF);
            bool fromPatch = (v >> 28) == 1;
            // Maps built to be refused point outside both files: there is no expected output.
            if ((v >> 28) > 1 || (fromPatch ? n >= patchBlocks.Length : (n + 1) * BS > baseBytes.Length))
                return (path, []);
            expected.AddRange(fromPatch ? patchBlocks[n] : baseBytes.AsSpan(n * BS, BS).ToArray());
        }
        expected.AddRange(sc);
        expected.AddRange(si);
        return (path, expected.ToArray());
    }

    private const uint P = 0x1000_0000;

    /// <summary>The FIH from the patch, base blocks moved and in place, patch blocks out of order.</summary>
    private static uint[] Map => [P | 1, 2, 3, 4, 1, P | 3, P | 2, 6, 7, 5, P | 4, P | 4, 9];

    private string Merge(string basePath, string patch)
    {
        var plan = LihMerge.Read(patch);
        LihMerge.CheckBase(plan, basePath);
        string output = Path.Combine(_dir, "out.pkg");
        File.Delete(output);
        LihMerge.Write(plan, basePath, patch, output);
        return output;
    }

    [Fact]
    public void RebuildsTheTargetBlockForBlock()
    {
        string basePath = BuildBase(10);
        var (patch, expected) = BuildPatch(Map, 3, basePath);
        var plan = LihMerge.Read(patch);
        Assert.Equal(Map.Length, plan.TargetBlocks);
        Assert.Equal(5, plan.PatchBlocks);
        Assert.Equal(expected.Length, plan.OutputLength);
        Assert.Equal(expected, File.ReadAllBytes(Merge(basePath, patch)));
    }

    [Fact]
    public void CoalescesConsecutiveSourceBlocksIntoRuns()
    {
        string basePath = BuildBase(10);
        var (patch, _) = BuildPatch(Map, 3, basePath);
        var runs = LihMerge.Read(patch).Runs;
        // P1 | 2-4 | 1 | P3 | P2 | 6-7 | 5 | P4 | P4 | 9
        Assert.Equal(10, runs.Count);
        Assert.Equal(new LihMerge.Run(false, 2, 1, 3), runs[1]);
        Assert.Equal(Map.Length, runs.Sum(r => r.Blocks));
    }

    [Fact]
    public void RefusesTheWrongBase()
    {
        string basePath = BuildBase(10, digest: new byte[32]);
        var (patch, _) = BuildPatch(Map, 3, basePath);
        var ex = Assert.Throws<InvalidDataException>(() => LihMerge.CheckBase(LihMerge.Read(patch), basePath));
        Assert.Contains("not the base the patch was built against", ex.Message);
    }

    [Fact]
    public void RefusesABaseTooShortForTheMap()
    {
        string basePath = BuildBase(10);
        var (patch, _) = BuildPatch([P | 1, 10], 0, basePath);   // block 10 is the ragged tail
        var ex = Assert.Throws<InvalidDataException>(() => LihMerge.CheckBase(LihMerge.Read(patch), basePath));
        Assert.Contains("past the base's end", ex.Message);
    }

    [Fact]
    public void RefusesAMapReadingPastThePatch()
    {
        string basePath = BuildBase(10);
        var (patch, _) = BuildPatch([P | 1, P | 999], 3, basePath);
        Assert.Contains("past the patch's end",
            Assert.Throws<InvalidDataException>(() => LihMerge.Read(patch)).Message);
    }

    [Fact]
    public void RefusesAnUnknownSourceKind()
    {
        string basePath = BuildBase(10);
        var (patch, _) = BuildPatch([P | 1, 0x2000_0001], 3, basePath);
        Assert.Contains("source kind 2",
            Assert.Throws<InvalidDataException>(() => LihMerge.Read(patch)).Message);
    }

    [Fact]
    public void RefusesAnFihWhoseCntOffsetDisagreesWithTheMap()
    {
        string basePath = BuildBase(10);
        var (patch, _) = BuildPatch(Map, 3, basePath, cntOffset: 0x1234_0000);
        Assert.Contains("puts its CNT at",
            Assert.Throws<InvalidDataException>(() => LihMerge.Read(patch)).Message);
    }

    [Fact]
    public void RefusesATruncatedPatch()
    {
        string basePath = BuildBase(10);
        var (patch, _) = BuildPatch(Map, 3, basePath);
        using (var f = File.OpenWrite(patch)) f.SetLength(f.Length - 1);
        Assert.Contains("truncated",
            Assert.Throws<InvalidDataException>(() => LihMerge.Read(patch)).Message);
    }

    [Fact]
    public void RefusesSegmentsThatDoNotTile()
    {
        string basePath = BuildBase(10);
        var (patch, _) = BuildPatch(Map, 3, basePath,
            tamperLih: lih => BinaryPrimitives.WriteUInt64LittleEndian(lih.AsSpan(0x38),
                BinaryPrimitives.ReadUInt64LittleEndian(lih.AsSpan(0x38)) - 1));
        Assert.Contains("do not tile",
            Assert.Throws<InvalidDataException>(() => LihMerge.Read(patch)).Message);
    }

    [Fact]
    public void TellsAnLihFromAPkg()
    {
        string basePath = BuildBase(4);
        var (patch, _) = BuildPatch([P | 1], 0, basePath);
        Assert.True(LihMerge.IsLih(patch));
        Assert.False(LihMerge.IsLih(basePath));
        Assert.Throws<InvalidDataException>(() => LihMerge.Read(basePath));
    }

    [Fact]
    public void NeverOverwritesAnExistingOutput()
    {
        string basePath = BuildBase(10);
        var (patch, _) = BuildPatch(Map, 3, basePath);
        string output = Path.Combine(_dir, "taken.pkg");
        File.WriteAllText(output, "keep me");
        Assert.Throws<IOException>(() => LihMerge.Write(LihMerge.Read(patch), basePath, patch, output));
        Assert.Equal("keep me", File.ReadAllText(output));
    }
}

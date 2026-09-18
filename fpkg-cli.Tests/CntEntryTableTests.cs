using Fpkg.Cli.RepairPlayGo;
using LibProsperoPkg.PKG;
using Xunit;

public class CntEntryTableTests
{
    [SkippableFact]
    public void ParsesTheKnownEntryTable()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        // This package's passcode happens to be 32 zeros — not a property of the format.
        var t = CntEntryTable.Parse(PackageRegions.Load(TestPackage.Path).Cnt, new string('0', 32));

        Assert.Equal(27, t.Physical.Count);
        Assert.Equal([16u, 32u, 128u, 256u, 1u, 512u, 8192u], t.Physical.Take(7).Select(e => e.Id));
        Assert.Equal(6736u, t[4097].DataSize);
        Assert.Equal(62u,   t[8209].DataSize);
        Assert.Equal(1981u, t[12288].DataSize);
        Assert.Equal("playgo-chunk.dat", t[4097].Name);
        Assert.True(t[1024].Encrypted);
        Assert.False(t[12288].Encrypted);
        // The five that feed main_ent_data_size, in physical order.
        Assert.Equal(7200u, (uint)t.Physical.Take(5).Sum(e => (long)e.DataSize));
    }

    [SkippableFact]
    public void PayloadsRoundTripAgainstTheLibrarysOwnReader()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        // This package's passcode happens to be 32 zeros — not a property of the format.
        var passcode = new string('0', 32);
        var t = CntEntryTable.Parse(PackageRegions.Load(TestPackage.Path).Cnt, passcode);
        foreach (uint id in new uint[] { 4097, 8208, 8209, 12288, 8192 })
        {
            var expected = ProsperoPackageArchive.TryReadCntEntry(TestPackage.Path, passcode, id);
            Assert.Equal(Convert.ToHexString(expected!), Convert.ToHexString(t[id].Payload));
        }
    }

    [SkippableFact]
    public void ThrowsWhenTheMetaTableIsNotSortedAscendingById()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var cnt = (byte[])PackageRegions.Load(TestPackage.Path).Cnt.Clone();
        int table = (int)CntHeader.U32(cnt, CntHeader.EntryTableOffset);

        // Swap the id fields (first 4 bytes) of the first two 32-byte meta records. The table is
        // ascending by id, so this makes record 0's id greater than record 1's — breaking the
        // invariant Parse relies on for positional digest-slot mapping.
        Span<byte> firstId = cnt.AsSpan(table, 4);
        Span<byte> secondId = cnt.AsSpan(table + 32, 4);
        Span<byte> tmp = stackalloc byte[4];
        firstId.CopyTo(tmp);
        secondId.CopyTo(firstId);
        tmp.CopyTo(secondId);

        Assert.Throws<InvalidDataException>(() => CntEntryTable.Parse(cnt, new string('0', 32)));
    }

    /// <summary>
    /// A missing entry is a property of the PACKAGE, not a caller bug. Program.PlayGoInitialChunkProblem
    /// reads only 4097 and 8209, so a container with a real PlayGo defect but no
    /// <c>playgo-scenario.json</c> (12288) sails past the "nothing to repair" gate and reaches
    /// guard 4's lookup. A bare <see cref="KeyNotFoundException"/> there is a .NET stack trace;
    /// this pins the clean refusal that names the entry instead.
    /// </summary>
    [SkippableFact]
    public void RefusesCleanlyWhenAnEntryIsMissing()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var cnt = DoctoredCntWithoutScenarioJson(TestPackage.Path);

        var t = CntEntryTable.Parse(cnt, new string('0', 32));
        Assert.Equal(26, t.Physical.Count);

        var ex = Assert.Throws<InvalidDataException>(() => t[12288]);
        Assert.Contains("12288", ex.Message);
    }

    /// <summary>
    /// Drops entry 12288's meta record by decrementing <c>entry_count</c>. 12288 is the highest id
    /// in this package, so it is the LAST record of the id-sorted on-disk table and dropping it
    /// leaves the table both sorted and contiguous — asserted here, because the trick is silently
    /// wrong for any other entry. The payload bytes stay where they are; nothing reads them once
    /// no meta record points at them.
    /// </summary>
    internal static byte[] DoctoredCntWithoutScenarioJson(string packagePath)
    {
        var cnt = (byte[])PackageRegions.Load(packagePath).Cnt.Clone();
        uint count = CntHeader.U32(cnt, CntHeader.EntryCount);
        int table = (int)CntHeader.U32(cnt, CntHeader.EntryTableOffset);
        Assert.Equal(12288u, CntHeader.U32(cnt, table + (int)(count - 1) * 32));

        CntHeader.SetU32(cnt, CntHeader.EntryCount, count - 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(
            cnt.AsSpan(CntHeader.EntryCount2), (ushort)(count - 1));
        return cnt;
    }

    [SkippableFact]
    public void MetaRecordsRoundTripThroughTheLibrarysCodec()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var cnt = PackageRegions.Load(TestPackage.Path).Cnt;
        // This package's passcode happens to be 32 zeros — not a property of the format.
        var t = CntEntryTable.Parse(cnt, new string('0', 32));
        int table = (int)CntHeader.U32(cnt, CntHeader.EntryTableOffset);
        foreach (var (e, i) in t.ById.Select((e, i) => (e, i)))
        {
            // MetaEntry.Write leaves the trailing 8 bytes of each 32-byte record untouched,
            // so compare only the 24 bytes it does write.
            Assert.Equal(
                Convert.ToHexString(cnt.AsSpan(table + i * 32, 24)),
                Convert.ToHexString(e.MetaBytes().AsSpan(0, 24)));
        }
    }
}

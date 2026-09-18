using Fpkg.Cli.RepairPlayGo;
using LibProsperoPkg.PKG;
using Xunit;

public class CntEntryTableTests
{
    [SkippableFact]
    public void ParsesTheKnownEntryTable()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var t = CntEntryTable.Parse(PackageRegions.Load(TestPackage.Path).Cnt);

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
        var t = CntEntryTable.Parse(PackageRegions.Load(TestPackage.Path).Cnt);
        var passcode = new string('0', 32);
        foreach (uint id in new uint[] { 4097, 8208, 8209, 12288, 8192 })
        {
            var expected = ProsperoPackageArchive.TryReadCntEntry(TestPackage.Path, passcode, id);
            Assert.Equal(Convert.ToHexString(expected!), Convert.ToHexString(t[id].Payload));
        }
    }

    [SkippableFact]
    public void MetaRecordsRoundTripThroughTheLibrarysCodec()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var cnt = PackageRegions.Load(TestPackage.Path).Cnt;
        var t = CntEntryTable.Parse(cnt);
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

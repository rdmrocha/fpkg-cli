using System.Buffers.Binary;
using Fpkg.Cli.RepairPlayGo;
using Xunit;

public class PackageRegionsTests
{
    [SkippableFact]
    public void SplitsTheKnownRegions()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var r = PackageRegions.Load(TestPackage.Path);
        Assert.Equal(0x10000, r.OuterPfsOffset);
        Assert.Equal(596_705_280, r.OuterPfsSize);
        Assert.Equal(596_770_816, r.CntOffset);
        Assert.Equal(63_569_920, r.Cnt.Length);
        Assert.Equal(665_774, r.Si.Length);

        // No padding past the body — Task 10's CRC length rule depends on this. CntHeader
        // (Task 3) doesn't exist yet, so these are read inline: big-endian body_offset at +32
        // and body_size at +40 in the CNT header. Task 3 will give these names.
        Assert.Equal((long)r.Cnt.Length,
            (long)BinaryPrimitives.ReadUInt64BigEndian(r.Cnt.AsSpan(32))    // body_offset
          + (long)BinaryPrimitives.ReadUInt64BigEndian(r.Cnt.AsSpan(40))); // body_size
    }

    [SkippableFact]
    public void SplicingBackTheOriginalRegionsReproducesTheFile()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var r = PackageRegions.Load(TestPackage.Path);
        var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        try
        {
            r.WriteTo(tmp, r.Cnt, r.Si);
            Assert.Equal(Bytes.Sha256(TestPackage.Path), Bytes.Sha256(tmp));
        }
        finally { File.Delete(tmp); }
    }
}

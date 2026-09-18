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

        // No padding past the body — Task 10's CRC length rule depends on this.
        Assert.Equal((long)r.Cnt.Length,
            (long)CntHeader.U64(r.Cnt, CntHeader.BodyOffset)
          + (long)CntHeader.U64(r.Cnt, CntHeader.BodySize));
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

    /// <summary>
    /// A CNT-only file is what Split's own cnt output is: ProsperoPkgReader.Read accepts it
    /// (DetectType reports Meta) but leaves Fih null, so Load must refuse it with a clear
    /// error rather than a NullReferenceException.
    /// </summary>
    [SkippableFact]
    public void RefusesAPackageWithNoFihHeader()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var cnt = PackageRegions.Load(TestPackage.Path).Cnt;
        var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        try
        {
            File.WriteAllBytes(tmp, cnt);
            var ex = Assert.Throws<InvalidDataException>(() => PackageRegions.Load(tmp));
            Assert.Contains("FIH", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(tmp); }
    }
}

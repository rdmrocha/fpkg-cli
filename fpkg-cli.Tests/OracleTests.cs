using Fpkg.Cli.RepairPlayGo;
using Xunit;

public class OracleTests
{
    [SkippableFact]
    public void TheOracleHasTheTargetEntrySizes()
    {
        Skip.IfNot(Oracle.Available, "oracle not built — see docs/superpowers/plans/2026-09-18-repair-playgo.md Task 7");
        // Original package's passcode happens to be 32 zeros — not a property of the format.
        var t = CntEntryTable.Parse(PackageRegions.Load(Oracle.Path).Cnt, new string('0', 32));
        Assert.Equal(5376u, t[4097].DataSize);
        Assert.Equal(62u,   t[8209].DataSize);
        Assert.Equal(2293u, t[12288].DataSize);

        // 661,006,510 (original) minus 661,005,150 (oracle) is 1,360 bytes — entirely the SI's
        // embedded playgo-chunk.dat shrinking from 6736 to 5376.
        Assert.Equal(661_005_150L, new FileInfo(Oracle.Path).Length);
    }

    [SkippableFact]
    public void TheOraclesOuterPfsIsIdenticalToTheOriginals()
    {
        Skip.IfNot(Oracle.Available && TestPackage.Exists, "oracle or test package not built — see docs/superpowers/plans/2026-09-18-repair-playgo.md Task 7");
        var a = PackageRegions.Load(TestPackage.Path);
        var b = PackageRegions.Load(Oracle.Path);
        Assert.Equal(a.OuterPfsSize, b.OuterPfsSize);

        var hashA = Bytes.Sha256Range(a.SourcePath, a.OuterPfsOffset, a.OuterPfsSize);
        var hashB = Bytes.Sha256Range(b.SourcePath, b.OuterPfsOffset, b.OuterPfsSize);
        Assert.Equal(hashA, hashB);
        Assert.StartsWith("3B7702FFC695B125", hashA, StringComparison.OrdinalIgnoreCase);
    }
}

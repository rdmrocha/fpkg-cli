using Fpkg.Cli.RepairPlayGo;
using Xunit;

public class CntRepairTests
{
    private static readonly string Passcode = new string('0', 32);

    /// <summary>
    /// FIXED POINT 2, CNT half. The repaired CNT must equal the oracle's CNT byte for byte.
    /// </summary>
    [SkippableFact]
    public void TheRepairedCntEqualsTheOracles()
    {
        Skip.IfNot(Oracle.Available && TestPackage.Exists, "oracle or test package not built");
        var orig = PackageRegions.Load(TestPackage.Path);
        var want = PackageRegions.Load(Oracle.Path).Cnt;

        var got = CntRepair.Repair(orig.Cnt, TestPackage.ContentId, Passcode);

        Assert.Equal(44_675, got.SlackBefore);
        Assert.Equal(-1048,  got.NetDelta);     // 4097 −1360, 8209 0, 12288 +312
        Bytes.AssertEqual(want, got.Cnt);
    }

    /// <summary>
    /// The net delta is negative on every package on disk, so the guard has to be provoked.
    /// </summary>
    [SkippableFact]
    public void RefusesWhenGrowthExceedsSlack()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        // Synthesise the condition. The repair's net delta on this package is a fixed -1048, so
        // shrinking body_size to leave a small POSITIVE slack would not provoke anything: -1048
        // fits in any non-negative slack. The guard fires on `NetDelta > SlackBefore`, so the
        // slack has to be pushed below -1048 — i.e. body_size shrunk until the existing layout
        // already overruns the declared body by more than the repair gives back (2048 > 1048).
        var cnt = PackageRegions.Load(TestPackage.Path).Cnt;
        var t = CntEntryTable.Parse(cnt, Passcode);
        var last = t.Physical[^1];
        ulong lastEnd = (ulong)last.DataOffset + last.DataSize;
        CntHeader.SetU64(cnt, CntHeader.BodySize,
            lastEnd - 2048 - CntHeader.U64(cnt, CntHeader.BodyOffset));

        var ex = Assert.Throws<InvalidOperationException>(
            () => CntRepair.Repair(cnt, TestPackage.ContentId, Passcode));
        Assert.Contains("slack", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // The four cases below cover the decision itself, including the POSITIVE-growth case that the
    // guard exists for and that no real package can reach: every package on disk shrinks. They need
    // no package, so they are plain [Fact]s and can never silently skip. The integration test above
    // is what proves the predicate is actually wired into Repair, ahead of Seal.

    [Fact]
    public void ANegativeDeltaWithAmplePositiveSlackFits()
    {
        // This package's real case: the body shrinks by 1048 with 44,675 bytes already spare.
        Assert.False(CntRepair.ExceedsSlack(netDelta: -1048, slackBefore: 44_675));
    }

    [Fact]
    public void APositiveDeltaWellWithinTheSlackFits()
    {
        Assert.False(CntRepair.ExceedsSlack(netDelta: 4_096, slackBefore: 44_675));
    }

    [Fact]
    public void APositiveDeltaBeyondTheSlackDoesNotFit()
    {
        // The case the guard exists for, unreachable through Repair on any package on disk.
        Assert.True(CntRepair.ExceedsSlack(netDelta: 44_676, slackBefore: 44_675));
    }

    [Fact]
    public void ADeltaExactlyEqualToTheSlackFits()
    {
        // The boundary is inclusive: the slack counts bytes that are genuinely available, so
        // consuming all of them puts the body end exactly on the declared end and overruns nothing.
        Assert.False(CntRepair.ExceedsSlack(netDelta: 44_675, slackBefore: 44_675));
    }
}

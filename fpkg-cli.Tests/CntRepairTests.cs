using System.Security.Cryptography;
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
    /// No package on disk can make the gate refuse — every one of them shrinks by a comfortable,
    /// in-step amount — so the refusal has to be synthesised. This shrinks the declared body_size
    /// until the EXISTING layout already overruns it by 2048 bytes, more than the 1048 the repair
    /// gives back, so the relayout cannot land back on the declared size. It is a pre-existing
    /// overrun, not growth: it exists to prove the gate is wired into Repair AHEAD of Seal, which
    /// the pure PredictBodySize tests below cannot show.
    /// </summary>
    [SkippableFact]
    public void RefusesWhenTheRelayoutWouldChangeBodySize()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var cnt = PackageRegions.Load(TestPackage.Path).Cnt;
        var t = CntEntryTable.Parse(cnt, Passcode);
        var last = t.Physical[^1];
        ulong lastEnd = (ulong)last.DataOffset + last.DataSize;
        CntHeader.SetU64(cnt, CntHeader.BodySize,
            lastEnd - 2048 - CntHeader.U64(cnt, CntHeader.BodyOffset));

        var ex = Assert.Throws<InvalidOperationException>(
            () => CntRepair.Repair(cnt, TestPackage.ContentId, Passcode));
        // "slack" appears only in OUR message; CntReseal.Seal's body_size exception does not
        // contain it. Asserting on it is what makes a mis-ordered check fail instead of pass.
        Assert.Contains("slack", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public void ARepairDoesNotModifyTheCallersBuffer()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var cnt = PackageRegions.Load(TestPackage.Path).Cnt;
        string before = Convert.ToHexString(SHA256.HashData(cnt));

        CntRepair.Repair(cnt, TestPackage.ContentId, Passcode);

        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(cnt)));
    }

    [SkippableFact]
    public void TheReportedChunkDatIsTheOraclesEntry4097()
    {
        Skip.IfNot(Oracle.Available && TestPackage.Exists, "oracle or test package not built");
        var want = CntEntryTable.Parse(PackageRegions.Load(Oracle.Path).Cnt, Passcode)[4097].Payload;

        var got = CntRepair.Repair(
            PackageRegions.Load(TestPackage.Path).Cnt, TestPackage.ContentId, Passcode);

        // The later outer-PFS splice consumes exactly these bytes, so they are pinned here rather
        // than left to hold only by reading Seal's and WriteEncrypted's buffer handling.
        Assert.Equal(Convert.ToHexString(want), Convert.ToHexString(got.NewChunkDat));
    }

    [SkippableFact]
    public void RefusesAContentIdThatIsNotTheHeaders()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var cnt = PackageRegions.Load(TestPackage.Path).Cnt;

        // Parse decrypts with the header's id; sealing under another would be silent corruption.
        Assert.Throws<ArgumentException>(
            () => CntRepair.Repair(cnt, "EP4060-PPSA00000_00-NOTTHISONEATALL0", Passcode));
    }

    // PredictBodySize replays Seal's own walk, so the cases below are the whole gate: they reach
    // both failure directions, which no package on disk can. They need no package, so they are
    // plain [Fact]s and can never silently skip.
    //
    // The fixture: body_offset 0x1000, 64-KiB body rounding, a 0x2F000-byte leading entry that ends
    // exactly on 0x30000 (a rounding step), then a tail entry of varying length. The original tail
    // is 0x9000, so the body ends at 0x39000, rounds to 0x40000 and body_size is 0x3F000.
    private const ulong BodyOffset = 0x1000;
    private const ulong K64 = 0x10000;
    private const uint Leading = 0x2F000;
    private const ulong OriginalBodySize = 0x3F000;

    private static ulong Predict(uint tail) =>
        CntRepair.PredictBodySize(BodyOffset, K64, [Leading, tail]);

    [Fact]
    public void TheFixturesOriginalLayoutReproducesItsBodySize()
    {
        Assert.Equal(OriginalBodySize, Predict(0x9000));
    }

    [Fact]
    public void AShrinkWithinTheRoundingStepKeepsBodySize()
    {
        Assert.Equal(OriginalBodySize, Predict(0x1000));
    }

    [Fact]
    public void AShrinkPastTheRoundingStepChangesBodySize()
    {
        // The tail vanishes, the body ends exactly on 0x30000, and the rounding drops a whole step.
        Assert.NotEqual(OriginalBodySize, Predict(0));
    }

    [Fact]
    public void AGrowthWithinTheRoundingStepKeepsBodySize()
    {
        Assert.Equal(OriginalBodySize, Predict(0xF000));
    }

    [Fact]
    public void AGrowthPastTheRoundingStepChangesBodySize()
    {
        Assert.NotEqual(OriginalBodySize, Predict(0x10001));
    }

    /// <summary>
    /// Both boundaries, and which side each falls on. Filling the step EXACTLY is accepted: the
    /// body end lands on 0x40000, which is already aligned, so nothing moves. One byte beyond it is
    /// refused. Going the other way, a single byte of tail is enough to hold the step — it aligns
    /// up to 16 and stays above 0x30000 — while zero drops it, so the shrink boundary is the first
    /// byte past a step rather than the step itself. This asymmetry is the alignment's doing, which
    /// is exactly what a raw length sum could not have seen.
    /// </summary>
    [Fact]
    public void TheRoundingStepBoundariesFallWhereTheAlignmentPutsThem()
    {
        Assert.Equal(OriginalBodySize,    Predict(0x10000));  // exactly fills the step: accepted
        Assert.NotEqual(OriginalBodySize, Predict(0x10001));  // one byte over: refused
        Assert.Equal(OriginalBodySize,    Predict(1));        // one byte holds the step: accepted
        Assert.NotEqual(OriginalBodySize, Predict(0));        // nothing left: the step is dropped
    }
}

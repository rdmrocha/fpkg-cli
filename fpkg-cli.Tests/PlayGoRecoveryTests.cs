using Fpkg.Cli.RepairPlayGo;
using Xunit;

public class PlayGoRecoveryTests
{
    [SkippableFact]
    public void RecoversTheMeasuredValues()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        // This package's passcode happens to be 32 zeros — not a property of the format.
        var t = CntEntryTable.Parse(PackageRegions.Load(TestPackage.Path).Cnt, new string('0', 32));
        var r = PlayGoRecovery.From(t[4097].Payload);

        Assert.Equal(100, r.ChunkCount);
        Assert.Equal(1,   r.ScenarioCount);
        Assert.Equal(0,   r.DefaultScenarioId);
        Assert.Equal(1,   r.DefaultLanguageId);
        Assert.Equal(101, r.ExtentCount);
        Assert.Equal(0xFFFFFFFFFFFFFFFFul, r.LanguageMask);
        Assert.Equal(0x23920000ul, r.TotalSize);
        Assert.Equal(0x23830000ul, r.DataSize);
        Assert.Equal(0xF0000ul,    r.TailSize);
        Assert.Equal(["Scenario #0"], r.ScenarioLabels);
        Assert.Equal(TestPackage.ContentId, r.ContentId);
    }
}

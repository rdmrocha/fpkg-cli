using Fpkg.Cli.RepairPlayGo;
using LibProsperoPkg.PlayGo;
using Xunit;

public class PlayGoEntriesTests
{
    private static readonly string Passcode = new string('0', 32);

    [SkippableFact]
    public void GeneratedEntriesAreByteIdenticalToTheOracles()
    {
        Skip.IfNot(Oracle.Available && TestPackage.Exists, "oracle or test package not built");
        var orig   = CntEntryTable.Parse(PackageRegions.Load(TestPackage.Path).Cnt, Passcode);
        var oracle = CntEntryTable.Parse(PackageRegions.Load(Oracle.Path).Cnt, Passcode);

        var built = PlayGoEntries.Build(PlayGoRecovery.From(orig[4097].Payload), orig[8209].Payload);

        Assert.Equal(Convert.ToHexString(oracle[4097].Payload),  Convert.ToHexString(built.ChunkDat));
        Assert.Equal(Convert.ToHexString(oracle[8209].Payload),  Convert.ToHexString(built.Ficm));
        Assert.Equal(Convert.ToHexString(oracle[12288].Payload), Convert.ToHexString(built.ScenarioJson));
    }

    [SkippableFact]
    public void TheGeneratedSetIsAValidPlayGoLayout()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var t = CntEntryTable.Parse(PackageRegions.Load(TestPackage.Path).Cnt, Passcode);
        var r = PlayGoRecovery.From(t[4097].Payload);
        var built = PlayGoEntries.Build(r, t[8209].Payload);

        var info = ProsperoPlayGo.ValidateLayout(
            built.ChunkDat, built.Ficm, t[8208].Payload, built.ScenarioJson, r.ContentId, null);

        Assert.Equal(100, info.ChunkCount);
        Assert.Equal(1,   info.ScenarioCount);
        Assert.Equal(33,  info.ExtentCount);          // 101 → 33
        Assert.Equal(0x23920000ul, info.CoveredBytes);
        Assert.Equal(23,  info.FileCount);
    }

    /// <summary>
    /// A minimal, self-contained PlayGoRecovery that needs no test package: a single chunk,
    /// single scenario, and a 36-character content id (the only shape BuildMultiChunkDat checks
    /// on it). Used only to reach PlayGoEntries.Build's FICM handling without the real package.
    /// </summary>
    private static PlayGoRecovery MinimalRecovery() => new(
        ChunkCount: 1, ScenarioCount: 1, DefaultScenarioId: 0, DefaultLanguageId: 1,
        ExtentCount: 1, LanguageMask: ulong.MaxValue, ContentId: new string('A', 36),
        TotalSize: 100, DataSize: 100, TailSize: 0, ScenarioLabels: ["Scenario #0"]);

    [Fact]
    public void FicmShorterThanTheHeaderThrows()
    {
        // No package needed: this guard fires before anything package-specific matters.
        var tooShort = new byte[10];
        Assert.Throws<InvalidDataException>(() => PlayGoEntries.Build(MinimalRecovery(), tooShort));
    }

    [Fact]
    public void FicmWithAPartialTrailingFileEntryThrows()
    {
        // 16-byte header plus one odd trailing byte: not a whole number of 2-byte file entries.
        var odd = new byte[17];
        Assert.Throws<InvalidDataException>(() => PlayGoEntries.Build(MinimalRecovery(), odd));
    }
}

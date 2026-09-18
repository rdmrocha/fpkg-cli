using System.Text;
using Fpkg.Cli.RepairPlayGo;
using Xunit;

public class RepairPlayGoCommandTests
{
    private static readonly string Passcode = new string('0', 32);

    /// <summary>Runs the command with stdout captured, and restores it whatever happens.</summary>
    private static string CaptureRun(string[] args)
    {
        var captured = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(captured);
            RepairPlayGoCommand.Run(args);
        }
        finally { Console.SetOut(previous); }
        return captured.ToString();
    }

    /// <summary>
    /// The user-visible complaint this fixes: the command used to run PlayGoInitialChunkProblem,
    /// PackageRegions.Load and CntEntryTable.Parse — seconds apiece on a 661 MB package — before
    /// printing a single line. The fix is ORDERING, so the assertion is about order, not presence.
    /// </summary>
    [SkippableFact]
    public void TheDryRunAnnouncesItselfBeforeTheSlowWork()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        string s = CaptureRun(["repair-playgo", TestPackage.Path]);

        int pkg = s.IndexOf("Package:", StringComparison.Ordinal);
        int problem = s.IndexOf("Problem:", StringComparison.Ordinal);
        Assert.True(pkg >= 0 && problem > pkg, "Package: must be printed before Problem:");
        Assert.Contains("reading package", s);
        // The stage banner has to come out before the report, not with it.
        Assert.True(s.IndexOf("reading package", StringComparison.Ordinal) < problem);
    }

    [SkippableFact]
    public void TheDryRunNumbersEveryStageItRuns()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        string s = CaptureRun(["repair-playgo", TestPackage.Path]);

        Assert.Contains("[1/4] reading package", s);
        Assert.Contains("[2/4] recovering PlayGo values", s);
        Assert.Contains("[3/4] generating replacement entries", s);
        Assert.Contains("[4/4] resealing the CNT", s);
    }

    [SkippableFact]
    public void VerboseAddsTheRecoveredValuesAndQuietDoesNot()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        Assert.DoesNotContain("chunkLanguageMasks", CaptureRun(["repair-playgo", TestPackage.Path]));
        Assert.Contains("chunkLanguageMasks", CaptureRun(["repair-playgo", TestPackage.Path, "--verbose"]));
    }

    [SkippableFact]
    public void VerboseAddsThePerEntryDigestsAndTheSiMemberList()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        string quiet = CaptureRun(["repair-playgo", TestPackage.Path]);
        string loud = CaptureRun(["repair-playgo", TestPackage.Path, "--verbose"]);

        Assert.DoesNotContain("SI member ", quiet);
        Assert.Contains($"SI member {SiRepair.ChunkDatPath}", loud);
        Assert.DoesNotContain("digest 4097", quiet);
        Assert.Contains("digest 4097", loud);
    }

    /// <summary>
    /// A --temp-dir that names an existing FILE is a typo. Directory.CreateDirectory would throw an
    /// IOException naming neither the flag nor the intent, so this refuses first and says which.
    /// (Where the journal actually lands is RepairJournal.PathFor's own test's business.)
    /// </summary>
    [Fact]
    public void TempDirPointingAtAFileRefusesCleanly()
    {
        var file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(file, "not a directory");
        var previous = Console.Error;
        var captured = new StringWriter();
        try
        {
            Console.SetError(captured);
            Assert.NotEqual(0, RepairPlayGoCommand.Run(
                ["repair-playgo", "nonexistent.pkg", "--temp-dir", file]));
        }
        finally { Console.SetError(previous); File.Delete(file); }

        Assert.Contains("--temp-dir", captured.ToString());
        Assert.Contains("existing file", captured.ToString());
        // And it refused rather than replacing the file with a directory.
        Assert.False(Directory.Exists(file));
    }

    [Fact]
    public void TempDirIsCreatedWhenItDoesNotExist()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            // The package is missing, so the run refuses — but --temp-dir is validated and created
            // first, which is the point being asserted.
            Assert.NotEqual(0, RepairPlayGoCommand.Run(
                ["repair-playgo", "nonexistent.pkg", "--temp-dir", dir]));
            Assert.True(Directory.Exists(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void TempDirWithoutAPathIsRefused() =>
        Assert.NotEqual(0, RepairPlayGoCommand.Run(
            ["repair-playgo", "nonexistent.pkg", "--temp-dir"]));

    [SkippableFact]
    public void DryRunIsTheDefaultAndWritesNothing()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var before = File.GetLastWriteTimeUtc(TestPackage.Path);
        Assert.Equal(0, RepairPlayGoCommand.Run(["repair-playgo", TestPackage.Path]));
        Assert.Equal(before, File.GetLastWriteTimeUtc(TestPackage.Path));
    }

    [Fact]
    public void RejectsOutAndInPlaceTogether() =>
        Assert.NotEqual(0, RepairPlayGoCommand.Run(
            ["repair-playgo", "nonexistent.pkg", "--out", "x.pkg", "--in-place"]));

    [Fact]
    public void RejectsAMissingFile() =>
        Assert.NotEqual(0, RepairPlayGoCommand.Run(["repair-playgo", "nonexistent.pkg"]));

    [Fact]
    public void RejectsAnUnknownFlag() =>
        Assert.NotEqual(0, RepairPlayGoCommand.Run(["repair-playgo", "nonexistent.pkg", "--nope"]));

    /// <summary>
    /// Guard 1. A wrong passcode is the one failure mode that is SILENT: the reseal would
    /// re-encrypt garbage and every digest would then verify over it, failing only on a console.
    /// The refusal must therefore happen before a single byte is written.
    /// </summary>
    [SkippableFact]
    public void RefusesAWrongPasscodeAndWritesNothing()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        Assert.NotEqual(0, RepairPlayGoCommand.Run(
            ["repair-playgo", TestPackage.Path, "--passcode", new string('1', 32), "--out", outPath]));
        Assert.False(File.Exists(outPath));
    }

    /// <summary>
    /// --in-place must produce exactly what --out produces from the same input, and must not
    /// leave its temporary file behind. Run on a COPY: the real package is an input to every
    /// other test in this project.
    /// </summary>
    [SkippableFact]
    public void InPlaceProducesTheSameBytesAsOutAndLeavesNoTempFile()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var dir = Directory.CreateTempSubdirectory("repair-playgo-inplace").FullName;
        try
        {
            var copy = Path.Combine(dir, "copy.pkg");
            File.Copy(TestPackage.Path, copy);
            var viaOut = Path.Combine(dir, "via-out.pkg");

            Assert.Equal(0, RepairPlayGoCommand.Run(
                ["repair-playgo", TestPackage.Path, "--out", viaOut]));
            Assert.Equal(0, RepairPlayGoCommand.Run(["repair-playgo", copy, "--in-place"]));

            Assert.Equal(Bytes.Sha256(viaOut), Bytes.Sha256(copy));
            Assert.Empty(Directory.GetFiles(dir, "*.repair-playgo.tmp"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// The property the whole design rests on: every guard runs before anything is written, so a
    /// refusal under --in-place leaves the target BYTE-IDENTICAL, not merely present. A wrong
    /// passcode is the cheapest trigger.
    /// </summary>
    [SkippableFact]
    public void AGuardRefusalUnderInPlaceLeavesTheTargetUntouched()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var dir = Directory.CreateTempSubdirectory("repair-playgo-refuse").FullName;
        try
        {
            var copy = Path.Combine(dir, "copy.pkg");
            File.Copy(TestPackage.Path, copy);
            var before = Bytes.Sha256(copy);

            Assert.NotEqual(0, RepairPlayGoCommand.Run(
                ["repair-playgo", copy, "--passcode", new string('1', 32), "--in-place"]));

            Assert.Equal(before, Bytes.Sha256(copy));
            Assert.Empty(Directory.GetFiles(dir, "*.repair-playgo.tmp"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// A package with a real PlayGo defect but no <c>playgo-scenario.json</c> (12288) passes the
    /// "nothing to repair" gate — which reads only entries 4097 and 8209 — and then reaches guard
    /// 4's lookup. The command must refuse with a non-zero exit, not die inside an indexer: this
    /// is the end-to-end proof that the lookup's InvalidDataException reaches the catch filter.
    /// </summary>
    [SkippableFact]
    public void RefusesAPackageMissingPlayGoScenarioJson()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var dir = Directory.CreateTempSubdirectory("repair-playgo-noscenario").FullName;
        try
        {
            var regions = PackageRegions.Load(TestPackage.Path);
            var doctored = Path.Combine(dir, "no-scenario.pkg");
            regions.WriteTo(doctored,
                            CntEntryTableTests.DoctoredCntWithoutScenarioJson(TestPackage.Path),
                            regions.Si);

            // Dry run: even the refusal path must not be reached via a write.
            Assert.NotEqual(0, RepairPlayGoCommand.Run(["repair-playgo", doctored]));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// Guard 4's negative path. The test package's own scenario JSON is generic, so without this
    /// the guard ships untested and the first package with localised names is its first test.
    /// </summary>
    [SkippableFact]
    public void RefusesAScenarioJsonCarryingCustomPresentation()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var t = CntEntryTable.Parse(PackageRegions.Load(TestPackage.Path).Cnt, Passcode);
        var r = PlayGoRecovery.From(t[4097].Payload);

        var generic = Encoding.UTF8.GetString(t[12288].Payload);
        var custom = Encoding.UTF8.GetBytes(generic.Replace("Scenario #0", "Terminator 2D: No Fate"));
        Assert.NotEqual(generic.Length, custom.Length);   // the substitution really happened

        RepairPlayGoCommand.EnsureScenarioJsonIsGeneric(t[12288].Payload, r);   // does not throw
        var ex = Assert.Throws<InvalidOperationException>(
            () => RepairPlayGoCommand.EnsureScenarioJsonIsGeneric(custom, r));
        Assert.Contains("custom scenario presentation", ex.Message);
    }
}

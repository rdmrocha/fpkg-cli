using System.Text;
using System.Text.RegularExpressions;
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
    /// printing a single line.
    ///
    /// <para>
    /// TWO assertions, each pinning one half, and neither satisfiable by the old ordering.
    /// </para>
    /// <para>
    /// <b>Package: is the FIRST line of output.</b> Not merely "before Problem:" — ReportPlan has
    /// always printed those two lines two apart in that order, so that assertion passed with the
    /// defect fully present. First-line is the user's actual complaint.
    /// </para>
    /// <para>
    /// <b>The stage-1 elapsed time covers the read.</b> The stage clock starts at its banner, so if
    /// the banner is printed AFTER Load has already returned the stage reports ~0 ms; it can only
    /// report the cost of reading the CNT and SI if it was opened before them. Reading ~64 MB does
    /// not happen in under 2 ms on any machine, and an already-finished load cannot take that long.
    /// This assertion replaces an older one on a <c>reading package N%</c> line: since Load stopped
    /// reading the whole payload the stage finishes in tens of milliseconds, and a stage that quick
    /// deliberately prints no percentage at all. Anchoring on a later line instead — an SI member,
    /// say — proves nothing: those are printed well after Load and Parse return, so a banner moved
    /// to sit between them still precedes it.
    /// </para>
    /// <para>
    /// THIS ONLY WORKS BECAUSE the read and the parse are separate stages. While stage 1 spanned
    /// Load, Parse and ReadContentId together, a banner moved to sit between Load and Parse still
    /// left Parse — seconds of it — on the clock, so the elapsed figure stayed far above 2 ms and
    /// this test passed on the very ordering it exists to reject. Split, "reading package" times
    /// nothing but Load, and that same move drops it to ~0 ms. Verified by experiment: moving the
    /// <c>Package:</c> line and the stage banner below Load makes this assertion fail.
    /// </para>
    /// </summary>
    [SkippableFact]
    public void TheDryRunAnnouncesItselfBeforeTheSlowWork()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        string s = CaptureRun(["repair-playgo", TestPackage.Path]);

        string firstLine = s.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.StartsWith("Package:", firstLine);

        int banner = s.IndexOf("[1/5] reading package", StringComparison.Ordinal);
        Assert.True(banner >= 0, "the stage 1 banner was never printed");

        var elapsed = Regex.Match(s, @"reading package done in (\d+(?:\.\d+)?)(ms|s)");
        Assert.True(elapsed.Success, "stage 1 never closed with an elapsed time");
        Assert.True(banner < elapsed.Index, "the banner must precede its own elapsed time");
        double ms = double.Parse(elapsed.Groups[1].Value) * (elapsed.Groups[2].Value == "s" ? 1000 : 1);
        Assert.True(ms >= 2,
                    $"stage 1 took {ms}ms, too little to have covered reading the package: it was " +
                    "announced after the slow work rather than before it");
    }

    [SkippableFact]
    public void TheDryRunNumbersEveryStageItRuns()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        string s = CaptureRun(["repair-playgo", TestPackage.Path]);

        Assert.Contains("[1/5] reading package", s);
        Assert.Contains("[2/5] parsing the entry table", s);
        Assert.Contains("[3/5] recovering PlayGo values", s);
        Assert.Contains("[4/5] generating replacement entries", s);
        Assert.Contains("[5/5] resealing the CNT", s);
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
    /// A --work-dir that names an existing FILE is a typo. Directory.CreateDirectory would throw an
    /// IOException naming neither the flag nor the intent, so this refuses first and says which.
    /// (Where the journal actually lands is RepairJournal.PathFor's own test's business.)
    /// </summary>
    [Fact]
    public void WorkDirPointingAtAFileRefusesCleanly()
    {
        var file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(file, "not a directory");
        var previous = Console.Error;
        var captured = new StringWriter();
        try
        {
            Console.SetError(captured);
            Assert.NotEqual(0, RepairPlayGoCommand.Run(
                ["repair-playgo", "nonexistent.pkg", "--work-dir", file]));
        }
        finally { Console.SetError(previous); File.Delete(file); }

        Assert.Contains("--work-dir", captured.ToString());
        Assert.Contains("existing file", captured.ToString());
        // And it refused rather than replacing the file with a directory.
        Assert.False(Directory.Exists(file));
    }

    /// <summary>
    /// A run that refuses before it starts must leave NOTHING behind — including the --work-dir it
    /// would otherwise have created. A flag that only relocates the journal or staging file has no
    /// business leaving a permanent directory on a run that never began.
    /// </summary>
    [Fact]
    public void ARefusedRunDoesNotLeaveItsWorkDirBehind()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            Assert.NotEqual(0, RepairPlayGoCommand.Run(
                ["repair-playgo", "nonexistent.pkg", "--work-dir", dir]));
            Assert.False(Directory.Exists(dir),
                         "a run that refused on its package still created its --work-dir");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    /// <summary>
    /// The dry run reaches no writer, so nothing ever lands in --work-dir — but the directory is
    /// still created, which is what proves the flag was honoured rather than ignored.
    /// </summary>
    [SkippableFact]
    public void WorkDirIsCreatedWhenItDoesNotExist()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            Assert.Equal(0, RepairPlayGoCommand.Run(
                ["repair-playgo", TestPackage.Path, "--work-dir", dir]));
            Assert.True(Directory.Exists(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void WorkDirWithoutAPathIsRefused()
    {
        var previous = Console.Error;
        var captured = new StringWriter();
        try
        {
            Console.SetError(captured);
            // A bare --work-dir parses to a null value, which must be refused BY NAME rather than
            // silently falling through to the default placement.
            Assert.NotEqual(0, RepairPlayGoCommand.Run(
                ["repair-playgo", "nonexistent.pkg", "--work-dir"]));
        }
        finally { Console.SetError(previous); }

        Assert.Contains("--work-dir needs a path", captured.ToString());
    }

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
    ///
    /// <para>
    /// The digest and the absent .tmp do NOT on their own prove --in-place took the in-place path:
    /// the staged writer renames its temporary onto the target, so an --in-place routed back
    /// through it would satisfy both. The stage lines are what distinguish them — the journal stage
    /// and the 8-stage total exist only on the in-place path, "staging pass" only on the staged one
    /// — so they are asserted here too. The same hole was fixed once already in
    /// <c>AcceptanceTests.InPlaceAndOutProduceIdenticalBytes</c>; this test differs from it in
    /// needing no oracle, so it still earns its place.
    /// </para>
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

            var captured = new StringWriter();
            var previous = Console.Out;
            try
            {
                Console.SetOut(captured);
                Assert.Equal(0, RepairPlayGoCommand.Run(["repair-playgo", copy, "--in-place"]));
            }
            finally { Console.SetOut(previous); }
            string output = captured.ToString();

            Assert.Equal(Bytes.Sha256(viaOut), Bytes.Sha256(copy));
            Assert.Empty(Directory.GetFiles(dir, "*.repair-playgo.tmp"));
            Assert.Empty(Directory.GetFiles(dir, "*" + RepairJournal.Suffix));
            Assert.Empty(Directory.GetFiles(dir, "*" + RepairJournal.MarkerSuffix));

            Assert.Contains("[6/9] journalling the original CNT and SI", output);
            Assert.Contains("[9/9] appending the rebuilt SI", output);
            Assert.DoesNotContain("staging pass", output);
            // The journal path is printed on every in-place run, not only under --verbose: it is
            // the user's only record of where the recovery data went, and they need it at the
            // moment they can no longer ask the command.
            Assert.Contains("Journal:    " + RepairJournal.PathFor(copy, null), output);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// The journal line belongs to the in-place mode alone: the other two write no journal, and a
    /// path announced for one that will never exist is worse than no line at all.
    /// </summary>
    [SkippableFact]
    public void OnlyTheInPlaceModeAnnouncesAJournalPath()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        Assert.DoesNotContain("Journal:", CaptureRun(["repair-playgo", TestPackage.Path]));
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

    /// <summary>
    /// THE regression test for the --out path writing the package twice: its two passes both went
    /// through <c>PackageRegions.WriteTo</c>, which starts with a create-and-truncate, so the
    /// second pass rewrote the whole payload to replace a ~660 KB SI tail. A user watched their
    /// 90 GB package's temporary file grow to 80 GB, vanish and regrow.
    ///
    /// <para>
    /// The anchor is BYTES WRITTEN, counted at the staging file itself. It has to be: the output
    /// was always byte-correct — the digest gates in <c>AcceptanceTests</c> passed throughout the
    /// bug's life and cannot see it. Nor can the absence of a temporary, or the stage lines, or
    /// the file's final length. Only the volume of writing distinguishes one pass over the payload
    /// from two, so that is what is asserted, against the one-pass figure rather than merely
    /// "less than twice".
    /// </para>
    /// <para>
    /// Deliberately NOT a tolerance wide enough to swallow a payload: the margin is 1 MiB on a
    /// ~630 MB package, so restoring the second <c>WriteTo</c> fails this by ~630 MB.
    /// </para>
    /// </summary>
    [SkippableFact]
    public void OutWritesThePayloadOnceNotTwice()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var dir = Directory.CreateTempSubdirectory("repair-playgo-once").FullName;
        try
        {
            var regions = PackageRegions.Load(TestPackage.Path);
            string contentId = CntHeader.ReadContentId(regions.Cnt);
            var repaired = CntRepair.Repair(regions.Cnt, contentId, Passcode);
            var target = Path.Combine(dir, "out.pkg");

            long written = 0;
            RepairPlayGoCommand.WriteRepaired(
                regions, repaired, contentId, target, workDir: null,
                progress: new Progress(TextWriter.Null, false, false, 8),
                openStagingFile: (path, mode) => new CountingStream(
                    new FileStream(path, mode, FileAccess.ReadWrite, FileShare.None),
                    n => written += n));

            long payloadAndCnt = regions.CntOffset + repaired.Cnt.LongLength;
            long si = new FileInfo(target).Length - payloadAndCnt;
            Assert.True(si > 0, "the repaired package must end with an SI");

            long once = payloadAndCnt + si;
            Assert.InRange(written, once, once + 1024 * 1024);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// A pass-through stream that counts the bytes written through it. Named for what it is: a
    /// test seam, not a production wrapper. It forwards <c>Seek</c> and <c>SetLength</c> because
    /// the second pass seeks to the end of the CNT and truncates there.
    /// </summary>
    private sealed class CountingStream(Stream inner, Action<long> counted) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] b, int o, int c) => inner.Read(b, o, c);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] b, int o, int c)
        {
            inner.Write(b, o, c);
            counted(c);
        }
        public override void Write(ReadOnlySpan<byte> b)
        {
            inner.Write(b);
            counted(b.Length);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

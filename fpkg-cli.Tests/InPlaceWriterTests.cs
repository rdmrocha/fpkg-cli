using Fpkg.Cli.RepairPlayGo;
using Xunit;

/// <summary>
/// The in-place writer is the one place that mutates the user's own package, so these tests hold
/// it to the same byte-exact standard as the staged writer (gate 1: the oracle's SHA-256) and add
/// the two properties that only in-place writing can violate: the ~600 MB outer payload must never
/// be rewritten, and the journal must be gone once the write has committed.
/// </summary>
public class InPlaceWriterTests
{
    private static readonly string Passcode = new string('0', 32);

    [SkippableFact]
    public void ProducesTheSameBytesAsTheStagedWriterAndLeavesNoJournal()
    {
        Skip.IfNot(TestPackage.Exists && Oracle.Available, "artifacts not present");
        var copy = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        var journal = copy + RepairJournal.Suffix;
        try
        {
            File.Copy(TestPackage.Path, copy);
            var regions = PackageRegions.Load(copy);
            var repaired = CntRepair.Repair(regions.Cnt, CntHeader.ReadContentId(regions.Cnt), Passcode);

            InPlaceWriter.Write(regions, repaired, CntHeader.ReadContentId(regions.Cnt),
                                copy, journal, new Progress(TextWriter.Null, false, false, 4));

            Assert.False(File.Exists(journal), "the journal must be gone after a successful write");
            Assert.Equal(Bytes.Sha256(Oracle.Path), Bytes.Sha256(copy));
            Assert.Equal(new FileInfo(Oracle.Path).Length, new FileInfo(copy).Length);
        }
        finally { File.Delete(copy); File.Delete(journal); }
    }

    [SkippableFact]
    public void TheOuterPayloadIsNeverRewritten()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var copy = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        var journal = copy + RepairJournal.Suffix;
        try
        {
            File.Copy(TestPackage.Path, copy);
            var regions = PackageRegions.Load(copy);
            var payloadBefore = Bytes.Sha256Range(copy, regions.OuterPfsOffset, regions.OuterPfsSize);
            var repaired = CntRepair.Repair(regions.Cnt, CntHeader.ReadContentId(regions.Cnt), Passcode);

            InPlaceWriter.Write(regions, repaired, CntHeader.ReadContentId(regions.Cnt),
                                copy, journal, new Progress(TextWriter.Null, false, false, 4));

            Assert.Equal(payloadBefore, Bytes.Sha256Range(copy, regions.OuterPfsOffset, regions.OuterPfsSize));
        }
        finally { File.Delete(copy); File.Delete(journal); }
    }

    /// <summary>
    /// A journal already on disk is the sole surviving copy of an interrupted repair's original
    /// CNT and SI, so the first act of a naive re-run would be to truncate the only recovery data
    /// there is. <see cref="RepairJournal.Write"/> opens with <c>FileMode.CreateNew</c> and refuses
    /// it at the syscall; this pins the writer's own earlier, cheaper refusal, which must leave both
    /// the journal and the package untouched.
    /// </summary>
    [SkippableFact]
    public void RefusesToRunWhenAJournalFromAnInterruptedRepairIsStillPresent()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var copy = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        var journal = copy + RepairJournal.Suffix;
        try
        {
            File.Copy(TestPackage.Path, copy);
            var regions = PackageRegions.Load(copy);
            var repaired = CntRepair.Repair(regions.Cnt, CntHeader.ReadContentId(regions.Cnt), Passcode);

            var sentinel = new byte[] { 1, 2, 3, 4 };
            File.WriteAllBytes(journal, sentinel);
            var before = Bytes.Sha256(copy);

            var e = Assert.Throws<InvalidOperationException>(() =>
                InPlaceWriter.Write(regions, repaired, CntHeader.ReadContentId(regions.Cnt),
                                    copy, journal, new Progress(TextWriter.Null, false, false, 4)));

            Assert.Contains(journal, e.Message);
            // The refusal has to come BEFORE anything is written: neither the journal's bytes nor
            // the package may have been touched.
            Assert.Equal(sentinel, File.ReadAllBytes(journal));
            Assert.Equal(before, Bytes.Sha256(copy));
        }
        finally { File.Delete(copy); File.Delete(journal); }
    }

    /// <summary>
    /// THE recoverability property, and the reason the journal exists at all: if the write dies in
    /// the window where the CNT has been spliced but the SI has not, the journal must still be on
    /// disk, complete, and able to put the package back exactly as it was.
    ///
    /// <para>
    /// Asserted by a test rather than by reading the code, because the code reads correct by
    /// accident: it holds only while <see cref="InPlaceWriter.Write"/> has no <c>try</c>/
    /// <c>finally</c> around the write and deletes the journal outside the <c>using</c>. Adding
    /// either — a natural-looking tidy-up — would silently destroy recoverability and leave every
    /// other test in this file green.
    /// </para>
    /// </summary>
    [SkippableFact]
    public void AFailureAfterTheCntIsSplicedLeavesAJournalThatRestoresThePackage()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var copy = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        var journal = copy + RepairJournal.Suffix;
        var reference = copy + ".reference-journal";
        try
        {
            File.Copy(TestPackage.Path, copy);
            var original = Bytes.Sha256(copy);
            var originalLength = new FileInfo(copy).Length;
            var regions = PackageRegions.Load(copy);
            var silent = new Progress(TextWriter.Null, false, false, 4);

            // What a correct journal for this pristine package looks like, written independently.
            // Comparing against it catches a journal that is present but truncated or rewritten,
            // which File.Exists alone would not.
            RepairJournal.Write(reference, copy, regions, silent);
            var referenceBytes = File.ReadAllBytes(reference);

            var repaired = CntRepair.Repair(regions.Cnt, CntHeader.ReadContentId(regions.Cnt), Passcode);

            var boom = Assert.Throws<IOException>(() =>
                InPlaceWriter.Write(regions, repaired, CntHeader.ReadContentId(regions.Cnt),
                                    copy, journal, silent,
                                    faultAfterCntWrite: () => throw new IOException("simulated mid-write failure")));
            Assert.Equal("simulated mid-write failure", boom.Message);

            // Non-vacuous: the fault really did fire mid-write, so the package on disk is now
            // neither the original nor the repair.
            Assert.NotEqual(original, Bytes.Sha256(copy));

            Assert.True(File.Exists(journal), "the journal must survive a failed write");
            Assert.Equal(referenceBytes, File.ReadAllBytes(journal));

            RepairJournal.Restore(journal, copy, silent);
            Assert.Equal(original, Bytes.Sha256(copy));
            Assert.Equal(originalLength, new FileInfo(copy).Length);
        }
        finally { File.Delete(copy); File.Delete(journal); File.Delete(reference); }
    }

    /// <summary>
    /// The other half of the recoverability property, from the CLI's side: a journal found at the
    /// top of a run means the previous run died mid-write, so the run must put the package back and
    /// stop — not repair the half-written file it found.
    /// </summary>
    [SkippableFact]
    public void AnInterruptedRepairIsRestoredOnTheNextRun()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var copy = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        var journal = copy + RepairJournal.Suffix;
        try
        {
            File.Copy(TestPackage.Path, copy);
            var before = Bytes.Sha256(copy);
            var regions = PackageRegions.Load(copy);

            // Simulate a crash: journal written, CNT half-written, SI never reached. The file's
            // length is untouched, which is exactly what tells recovery the repair never finished.
            RepairJournal.Write(journal, copy, regions, new Progress(TextWriter.Null, false, false, 1));
            using (var fs = new FileStream(copy, FileMode.Open, FileAccess.Write))
            {
                fs.Position = regions.CntOffset;
                fs.Write(new byte[1_000_000]);
            }
            Assert.NotEqual(before, Bytes.Sha256(copy));

            Assert.Equal(0, RepairPlayGoCommand.Run(["repair-playgo", copy, "--in-place"]));

            Assert.Equal(before, Bytes.Sha256(copy));
            Assert.False(File.Exists(journal), "the journal must be removed after a successful restore");
        }
        finally { File.Delete(copy); File.Delete(journal); }
    }

    /// <summary>
    /// The destructive case, and the reason recovery discriminates on the file's LENGTH rather than
    /// on whether the package still looks broken.
    ///
    /// <para>
    /// If <c>File.Delete</c> fails at the very end of a COMMITTED repair, the journal outlives a
    /// good result. Naive recovery would restore the original and silently undo a success. Worse,
    /// the obvious discriminator — asking <c>PlayGoInitialChunkProblem</c> whether the package still
    /// needs repairing — gets the truly dangerous case backwards: a package interrupted mid-write
    /// carries the REPAIRED CNT with a stale SI, so the detector reads entries 4097/8209 from the
    /// repaired CNT, reports "nothing wrong", and skips restoring the one file that most needs it.
    /// </para>
    /// </summary>
    [SkippableFact]
    public void AStaleJournalAfterACompletedRepairIsDiscardedInsteadOfRollingItBack()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var copy = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        var journal = copy + RepairJournal.Suffix;
        var setAside = copy + ".set-aside-journal";
        try
        {
            File.Copy(TestPackage.Path, copy);
            var regions = PackageRegions.Load(copy);
            var contentId = CntHeader.ReadContentId(regions.Cnt);
            var silent = new Progress(TextWriter.Null, false, false, 4);

            // The journal the repair itself writes, kept aside so it can be put back afterwards —
            // which is precisely the state a failing File.Delete at step 5 leaves on disk.
            RepairJournal.Write(setAside, copy, regions, silent);

            var repaired = CntRepair.Repair(regions.Cnt, contentId, Passcode);
            InPlaceWriter.Write(regions, repaired, contentId, copy, journal, silent);

            var repairedSha = Bytes.Sha256(copy);
            var repairedLength = new FileInfo(copy).Length;
            // Non-vacuous: the discriminator only works because a completed repair changes the
            // file's length. If this ever stopped holding, the test below would pass for the wrong
            // reason.
            Assert.NotEqual(new FileInfo(TestPackage.Path).Length, repairedLength);

            File.Move(setAside, journal);

            Assert.Equal(0, RepairPlayGoCommand.Run(["repair-playgo", copy, "--in-place"]));

            Assert.Equal(repairedSha, Bytes.Sha256(copy));
            Assert.Equal(repairedLength, new FileInfo(copy).Length);
            Assert.False(File.Exists(journal), "a stale journal must be removed, not acted on");
        }
        finally { File.Delete(copy); File.Delete(journal); File.Delete(setAside); }
    }

    /// <summary>
    /// A journal that belongs to some OTHER package must be acted on in neither direction. Restoring
    /// from it would write one package's regions over another; discarding it would destroy the other
    /// package's only way back. Both are unrecoverable, so the only correct answer is to refuse and
    /// let a human sort out which package the journal belongs to.
    ///
    /// <para>
    /// Not covered by the length discriminator: a foreign journal's <c>TargetLength</c> generally
    /// differs from the target's, which on its own reads as "a completed repair, discard the
    /// journal" — the destructive answer.
    /// </para>
    /// </summary>
    [SkippableFact]
    public void AJournalBelongingToAnotherPackageIsNeitherRestoredNorDiscarded()
    {
        Skip.IfNot(TestPackage.Exists && Oracle.Available, "artifacts not present");
        var mine = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        var theirs = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        var theirJournal = theirs + RepairJournal.Suffix;
        var mineJournal = mine + RepairJournal.Suffix;
        try
        {
            File.Copy(TestPackage.Path, theirs);
            File.Copy(Oracle.Path, mine);
            RepairJournal.Write(theirJournal, theirs, PackageRegions.Load(theirs),
                                new Progress(TextWriter.Null, false, false, 1));
            // Same bytes, now sitting where MY package's journal would live.
            File.Move(theirJournal, mineJournal);

            var before = Bytes.Sha256(mine);
            var journalBefore = Bytes.Sha256(mineJournal);

            Assert.NotEqual(0, RepairPlayGoCommand.Run(["repair-playgo", mine, "--in-place"]));

            Assert.Equal(before, Bytes.Sha256(mine));
            Assert.True(File.Exists(mineJournal), "a foreign journal must not be destroyed either");
            Assert.Equal(journalBefore, Bytes.Sha256(mineJournal));
        }
        finally { File.Delete(mine); File.Delete(theirs); File.Delete(mineJournal); File.Delete(theirJournal); }
    }

    /// <summary>
    /// The load-bearing invariant. In-place writing is only sound because the repaired CNT occupies
    /// exactly the footprint of the one it replaces; a shorter one would leave a hole and a longer
    /// one would overwrite live bytes. The only correct response is to refuse — never to pad,
    /// resize, or quietly fall back to staging — so the refusal is pinned here. No package needed:
    /// the guard runs before anything is opened.
    /// </summary>
    [Fact]
    public void RefusesWhenTheRepairedCntWouldNotFitItsOwnFootprint()
    {
        var regions = new PackageRegions("unused.pkg", 0x10000, 0x1000, 0x11000,
                                         new byte[100], new byte[10]);
        var mismatched = new CntRepairResult(new byte[99], new byte[4], 0, -1);
        var journal = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".journal");

        var e = Assert.Throws<InvalidOperationException>(() =>
            InPlaceWriter.Write(regions, mismatched, "CONTENT-ID", "unused.pkg", journal,
                                new Progress(TextWriter.Null, false, false, 4)));

        Assert.Contains("99", e.Message);
        Assert.Contains("100", e.Message);
        // It must refuse before journalling, not after: RepairJournal.Write would otherwise have
        // created a sidecar for a repair that can never run.
        Assert.False(File.Exists(journal));
    }
}

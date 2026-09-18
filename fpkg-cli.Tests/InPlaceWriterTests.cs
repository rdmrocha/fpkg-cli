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
    /// CNT and SI, and <see cref="RepairJournal.Write"/> opens with <c>FileMode.Create</c>, so the
    /// first act of a naive re-run would be to truncate the only recovery data there is. The
    /// refusal is enforced here, at the point of danger, not left to the caller.
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

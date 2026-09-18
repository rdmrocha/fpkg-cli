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
}

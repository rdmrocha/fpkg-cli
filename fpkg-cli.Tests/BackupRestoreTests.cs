using Fpkg.Cli.RepairPlayGo;
using Xunit;

/// <summary>
/// The retained backup: the same CNT+SI regions the crash journal already captures, kept after a
/// successful repair instead of deleted, so an in-place repair can be undone without a rebuild.
/// A bisection over several candidate fixes needs exactly one of these — every in-place fix
/// rewrites the same two regions, so one pristine copy restores the baseline each time.
/// </summary>
public class BackupRestoreTests
{
    private static readonly string Passcode = new string('0', 32);

    private static Progress Quiet() => new(TextWriter.Null, false, false, 4);

    [SkippableFact]
    public void KeepingTheBackupLeavesTheSidecarAndRemovesTheJournalAndMarker()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var copy = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        var journal = copy + RepairJournal.Suffix;
        var backup = RepairJournal.BackupPathFor(copy);
        try
        {
            File.Copy(TestPackage.Path, copy);
            var regions = PackageRegions.Load(copy);
            var contentId = CntHeader.ReadContentId(regions.Cnt);

            InPlaceWriter.Write(regions, CntRepair.Repair(regions.Cnt, contentId, Passcode),
                                contentId, copy, journal, Quiet(), backupPath: backup);

            Assert.True(File.Exists(backup), "the backup must survive a successful repair");
            Assert.False(File.Exists(journal), "the journal must not — its presence means 'interrupted'");
            Assert.False(File.Exists(RepairJournal.MarkerPathFor(copy)),
                         "and neither must the marker that named it");
        }
        finally
        {
            foreach (var f in new[] { copy, journal, backup, RepairJournal.MarkerPathFor(copy) })
                File.Delete(f);
        }
    }

    [SkippableFact]
    public void RestoringFromTheBackupPutsThePackageBackByteForByte()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var copy = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        var journal = copy + RepairJournal.Suffix;
        var backup = RepairJournal.BackupPathFor(copy);
        try
        {
            File.Copy(TestPackage.Path, copy);
            var before = Bytes.Sha256(copy);
            var lengthBefore = new FileInfo(copy).Length;
            var regions = PackageRegions.Load(copy);
            var contentId = CntHeader.ReadContentId(regions.Cnt);

            InPlaceWriter.Write(regions, CntRepair.Repair(regions.Cnt, contentId, Passcode),
                                contentId, copy, journal, Quiet(), backupPath: backup);
            // Guards the assertion below: if the repair were a no-op the round trip would pass
            // without proving anything.
            Assert.NotEqual(before, Bytes.Sha256(copy));

            RepairJournal.Restore(backup, copy, Quiet());

            Assert.Equal(lengthBefore, new FileInfo(copy).Length);
            Assert.Equal(before, Bytes.Sha256(copy));
        }
        finally
        {
            foreach (var f in new[] { copy, journal, backup, RepairJournal.MarkerPathFor(copy) })
                File.Delete(f);
        }
    }

    /// <summary>
    /// A backup only exists because an in-place run made one. Silently accepting the flag on an
    /// --out or dry run would promise an undo that was never written.
    /// </summary>
    [Fact]
    public void KeepBackupWithoutInPlaceIsRefused()
    {
        var code = RepairPlayGoCommand.Run(["repair-playgo", "/nonexistent.pkg", "--keep-backup"]);

        Assert.NotEqual(0, code);
    }

    /// <summary>
    /// The whole undo story through the real entry point: repair in place keeping the backup, then
    /// restore from it, and land on the original bytes. This is the loop a bisection runs — patch,
    /// test on hardware, restore, patch differently — so it is asserted end to end rather than at
    /// the writer seam alone.
    /// </summary>
    [SkippableFact]
    public void TheCommandCanRepairInPlaceAndThenRestoreFromItsBackup()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var copy = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        var backup = RepairJournal.BackupPathFor(copy);
        try
        {
            File.Copy(TestPackage.Path, copy);
            var before = Bytes.Sha256(copy);

            Assert.Equal(0, RepairPlayGoCommand.Run(
                ["repair-playgo", copy, "--in-place", "--keep-backup"]));
            Assert.True(File.Exists(backup));
            Assert.NotEqual(before, Bytes.Sha256(copy));

            Assert.Equal(0, RepairPlayGoCommand.Run(["repair-playgo", copy, "--restore"]));

            Assert.Equal(before, Bytes.Sha256(copy));
        }
        finally
        {
            foreach (var f in new[] { copy, backup, copy + RepairJournal.Suffix,
                                      RepairJournal.MarkerPathFor(copy) })
                File.Delete(f);
        }
    }

    /// <summary>
    /// The reason the backup gets its own suffix. RecoverIfNeeded reads the PRESENCE of a journal
    /// as "a repair was interrupted" and settles which side of the write a crash fell on by the
    /// file's length — so a retained journal would be deleted on the next run, and an SI that
    /// rebuilt to exactly the original length would be rolled back. A later run must see a package
    /// carrying a backup as simply repaired, and leave both it and the backup alone.
    ///
    /// The production change that breaks this test is giving the backup the journal's suffix.
    /// </summary>
    [SkippableFact]
    public void ALaterRunDoesNotMistakeARetainedBackupForAnInterruptedRepair()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var copy = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        var backup = RepairJournal.BackupPathFor(copy);
        try
        {
            File.Copy(TestPackage.Path, copy);
            Assert.Equal(0, RepairPlayGoCommand.Run(
                ["repair-playgo", copy, "--in-place", "--keep-backup"]));
            var repaired = Bytes.Sha256(copy);

            Assert.Equal(0, RepairPlayGoCommand.Run(["repair-playgo", copy, "--dry-run"]));

            Assert.Equal(repaired, Bytes.Sha256(copy));
            Assert.True(File.Exists(backup), "the backup must survive a later run of the command");
        }
        finally
        {
            foreach (var f in new[] { copy, backup, copy + RepairJournal.Suffix,
                                      RepairJournal.MarkerPathFor(copy) })
                File.Delete(f);
        }
    }
}

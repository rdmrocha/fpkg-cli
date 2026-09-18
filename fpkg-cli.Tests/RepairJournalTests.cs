using Fpkg.Cli.RepairPlayGo;
using Xunit;

public class RepairJournalTests
{
    [SkippableFact]
    public void RoundTripsAndRestoresAMutatedPackage()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var copy = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        var journal = copy + RepairJournal.Suffix;
        try
        {
            File.Copy(TestPackage.Path, copy);
            var regions = PackageRegions.Load(copy);
            var before = Bytes.Sha256(copy);
            var progress = new Progress(TextWriter.Null, false, false, 1);

            RepairJournal.Write(journal, copy, regions, progress);
            var read = RepairJournal.TryRead(journal);
            Assert.NotNull(read);
            Assert.Equal(new FileInfo(copy).Length, read!.TargetLength);
            Assert.Equal(regions.CntOffset, read.CntOffset);
            Assert.Equal(regions.Cnt.Length, read.CntLength);
            Assert.Equal(regions.Si.Length, read.SiLength);

            // Corrupt the CNT and the tail the way a half-finished repair would.
            using (var fs = new FileStream(copy, FileMode.Open, FileAccess.Write))
            {
                fs.Position = regions.CntOffset;
                fs.Write(new byte[4096]);
                fs.SetLength(fs.Length - 1_000);
            }
            Assert.NotEqual(before, Bytes.Sha256(copy));

            RepairJournal.Restore(journal, copy, progress);
            Assert.Equal(before, Bytes.Sha256(copy));
        }
        finally { File.Delete(copy); File.Delete(journal); }
    }

    [SkippableFact]
    public void RefusesToRestoreOverADifferentPackage()
    {
        Skip.IfNot(TestPackage.Exists && Oracle.Available, "artifacts not present");
        var copy = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        var other = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        var journal = copy + RepairJournal.Suffix;
        try
        {
            File.Copy(TestPackage.Path, copy);
            File.Copy(Oracle.Path, other);
            RepairJournal.Write(journal, copy, PackageRegions.Load(copy),
                                new Progress(TextWriter.Null, false, false, 1));
            var ex = Assert.Throws<InvalidDataException>(
                () => RepairJournal.Restore(journal, other, new Progress(TextWriter.Null, false, false, 1)));
            Assert.Contains("different package", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(copy); File.Delete(other); File.Delete(journal); }
    }

    [Fact]
    public void ATornJournalReadsAsNullRatherThanThrowing()
    {
        var p = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + RepairJournal.Suffix);
        try
        {
            File.WriteAllBytes(p, "FPKGJRN1"u8.ToArray());     // header only, no body, no digest
            Assert.Null(RepairJournal.TryRead(p));
            File.WriteAllBytes(p, new byte[8]);                // wrong magic
            Assert.Null(RepairJournal.TryRead(p));
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void PathForHonoursTempDir()
    {
        // Beside the package: named after it, nothing else — the directory already pairs them.
        Assert.Equal(Path.Combine("/pkgs", "a.pkg" + RepairJournal.Suffix),
                     RepairJournal.PathFor("/pkgs/a.pkg", null));

        // Under --temp-dir: in that directory, still recognisable by name, plus a disambiguator.
        var temp = RepairJournal.PathFor("/pkgs/a.pkg", "/tmp/x");
        Assert.Equal("/tmp/x", Path.GetDirectoryName(temp));
        Assert.StartsWith("a.pkg.", Path.GetFileName(temp));
        Assert.EndsWith(RepairJournal.Suffix, temp);
    }

    [Fact]
    public void PathForSeparatesSameNamedPackagesUnderOneTempDir()
    {
        // Two packages with the same name in different directories must not share a journal:
        // whichever repair started second would overwrite the first's recovery data, and an
        // interruption of the first would then be unrecoverable.
        Assert.NotEqual(RepairJournal.PathFor("/a/game.pkg", "/tmp/j"),
                        RepairJournal.PathFor("/b/game.pkg", "/tmp/j"));

        // Same package, same journal — the name must still be stable across runs.
        Assert.Equal(RepairJournal.PathFor("/a/game.pkg", "/tmp/j"),
                     RepairJournal.PathFor("/a/game.pkg", "/tmp/j"));
    }
}

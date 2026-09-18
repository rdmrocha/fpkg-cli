using System.Buffers.Binary;
using System.Security.Cryptography;
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

            // Restore must not consume the journal: recovery can itself be interrupted, and a
            // second attempt has to work. An edit that deleted the journal at the end of Restore
            // would break that silently.
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

            var otherBefore = Bytes.Sha256(other);
            var ex = Assert.Throws<InvalidDataException>(
                () => RepairJournal.Restore(journal, other, new Progress(TextWriter.Null, false, false, 1)));
            Assert.Contains("different package", ex.Message, StringComparison.OrdinalIgnoreCase);
            // A refusal must leave the target byte for byte as it was found — the identity is
            // checked before any write handle is opened, and Task 3's recovery dispatch relies
            // on that.
            Assert.Equal(otherBefore, Bytes.Sha256(other));

            // The path is only half the identity. Same path, different content must also refuse —
            // without this the test would still pass if ComputeIdentity hashed no file content at
            // all. Overwriting the FIH block is exactly the change the identity exists to catch.
            using (var fs = new FileStream(copy, FileMode.Open, FileAccess.Write))
                fs.Write(Enumerable.Repeat((byte)0xA5, 65_536).ToArray());
            var ex2 = Assert.Throws<InvalidDataException>(
                () => RepairJournal.Restore(journal, copy, new Progress(TextWriter.Null, false, false, 1)));
            Assert.Contains("different package", ex2.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(copy); File.Delete(other); File.Delete(journal); }
    }

    /// <summary>
    /// A well-formed journal, built by hand so the torn cases below can each break exactly one
    /// property. Layout: 72-byte header, payload, SHA-256 over everything preceding it.
    /// </summary>
    private static byte[] WellFormedJournal(byte[] cnt, byte[] si, string magic = "FPKGJRN1")
    {
        var body = new byte[72 + cnt.Length + si.Length];
        System.Text.Encoding.ASCII.GetBytes(magic).CopyTo(body, 0);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(8), 1_000L);   // TargetLength
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(16), 100L);    // CntOffset
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(24), cnt.LongLength);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(32), si.LongLength);
        // Identity at 40..72 stays zero: TryRead returns it verbatim and never interprets it.
        cnt.CopyTo(body, 72);
        si.CopyTo(body, 72 + cnt.Length);
        return [.. body, .. SHA256.HashData(body)];
    }

    [Fact]
    public void ATornJournalReadsAsNullRatherThanThrowing()
    {
        var p = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + RepairJournal.Suffix);
        try
        {
            // The baseline: without this, every assertion below could pass on a TryRead that
            // simply always returned null.
            var good = WellFormedJournal([1, 2, 3, 4], [9, 9]);
            File.WriteAllBytes(p, good);
            var read = RepairJournal.TryRead(p);
            Assert.NotNull(read);
            Assert.Equal(4, read!.CntLength);
            Assert.Equal(2, read.SiLength);

            File.WriteAllBytes(p, "FPKGJRN1"u8.ToArray());     // header only, no body, no digest
            Assert.Null(RepairJournal.TryRead(p));

            File.WriteAllBytes(p, new byte[8]);                // wrong magic, too short to boot
            Assert.Null(RepairJournal.TryRead(p));

            // Long enough to clear the header read, so this one actually reaches the magic check.
            var foreign = new byte[512];
            Random.Shared.NextBytes(foreign);
            "NOTAJRNL"u8.CopyTo(foreign);
            File.WriteAllBytes(p, foreign);
            Assert.Null(RepairJournal.TryRead(p));

            // Valid in every other respect — right lengths, right digest — and wrong only in its
            // magic. Nothing but the magic check can reject this one.
            File.WriteAllBytes(p, WellFormedJournal([1, 2, 3, 4], [9, 9], "FPKGJRN2"));
            Assert.Null(RepairJournal.TryRead(p));

            // Trailing garbage after a correct digest: the payload hashes correctly, so only the
            // declared-lengths-against-real-length check can reject it.
            File.WriteAllBytes(p, [.. good, 0, 0, 0]);
            Assert.Null(RepairJournal.TryRead(p));

            // Correct magic, absurd declared length: caught by comparing the declared lengths
            // against the file's real length, BEFORE anything is sized or seeked from them. A
            // reader that trusted the field would try to allocate 8 exabytes here.
            var lying = new byte[72];
            "FPKGJRN1"u8.CopyTo(lying);
            BinaryPrimitives.WriteInt64LittleEndian(lying.AsSpan(24), long.MaxValue);
            File.WriteAllBytes(p, lying);
            Assert.Null(RepairJournal.TryRead(p));

            // Well formed but for a single flipped payload byte: only the digest can catch this.
            var torn = (byte[])good.Clone();
            torn[73] ^= 0xFF;
            File.WriteAllBytes(p, torn);
            Assert.Null(RepairJournal.TryRead(p));

            // ... and a flipped digest byte, the same check from the other side.
            var badDigest = (byte[])good.Clone();
            badDigest[^1] ^= 0xFF;
            File.WriteAllBytes(p, badDigest);
            Assert.Null(RepairJournal.TryRead(p));

            Assert.Null(RepairJournal.TryRead(p + ".does-not-exist"));
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

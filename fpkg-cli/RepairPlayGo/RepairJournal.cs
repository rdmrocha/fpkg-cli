using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Fpkg.Cli.RepairPlayGo;

/// <summary>
/// The crash-recovery journal for an in-place repair: a sidecar file holding the two regions an
/// in-place write overwrites — the embedded CNT and the trailing SI — together with enough
/// metadata to put the package back exactly as it was.
///
/// <para>
/// An in-place repair writes the repaired CNT straight into its own footprint, so there is a
/// window (roughly the ~660 MB CRC pass) in which the file on disk carries a repaired CNT and a
/// stale SI: a damaged package. The journal closes that window. It is written and flushed to the
/// device <em>before</em> the package is opened for writing and deleted only once the repair has
/// completed, so a journal found on disk always means "an interrupted repair, undoable".
/// </para>
///
/// <para>On-disk layout — all integers little-endian:</para>
/// <code>
///  0   8   magic "FPKGJRN1"
///  8   8   TargetLength    original file length
/// 16   8   CntOffset
/// 24   8   CntLength
/// 32   8   SiLength
/// 40  32   Identity        see ComputeIdentity
/// 72   .   Cnt             CntLength bytes, the ORIGINAL CNT region
///  .   .   Si              SiLength bytes, the ORIGINAL SI region
///  .  32   Digest          SHA-256 over everything preceding it
/// </code>
/// </summary>
internal sealed record RepairJournal(
    long TargetLength, long CntOffset, long CntLength, long SiLength, byte[] Identity)
{
    internal const string Suffix = ".repair-playgo.journal";

    private static ReadOnlySpan<byte> Magic => "FPKGJRN1"u8;

    private const int HeaderLength = 72;
    private const int DigestLength = 32;
    private const int IdentityBlockLength = 65536;
    private const int CopyBuffer = 81920;

    /// <summary>
    /// Where the journal for <paramref name="target"/> lives: beside the package by default, or
    /// in <paramref name="tempDir"/> when one is given (same base name either way, so a stray
    /// journal is always traceable back to its package).
    /// </summary>
    internal static string PathFor(string target, string? tempDir)
    {
        string name = Path.GetFileName(target) + Suffix;
        string dir = string.IsNullOrEmpty(tempDir)
            ? Path.GetDirectoryName(target) ?? "."
            : tempDir;
        return Path.Combine(dir, name);
    }

    /// <summary>
    /// SHA-256 of file[0, 65536) — the FIH block — mixed with the package's file name.
    ///
    /// <para>
    /// The hashed <em>region</em> is deliberately one the repair never modifies. The obvious
    /// choice, hashing the CNT, would be wrong: at the very moment recovery needs the identity to
    /// match, the file on disk carries a half-written <em>repaired</em> CNT, so a CNT-based
    /// identity could never match. Everything below <see cref="PackageRegions.CntOffset"/> is
    /// untouched by an in-place write, and the FIH block is the cheap, always-present head of it.
    /// </para>
    ///
    /// <para>
    /// The file name is mixed in because the untouched region alone cannot distinguish a package
    /// from its own repaired rebuild — they share every byte below the CNT (the acceptance oracle
    /// and the test package are byte-identical for the first 596 MB). That is not a flaw in the
    /// region choice; it is inherent: any region an interrupted repair leaves intact is also left
    /// intact by a completed one. The file name is the one discriminator that survives a partial
    /// write, and it is exactly right for this job — an in-place repair recovers the same path it
    /// crashed on. The cost is that renaming a package after a crash makes its journal
    /// inapplicable; that is the safe direction to fail.
    /// </para>
    /// </summary>
    internal static byte[] ComputeIdentity(string packagePath)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var input = File.OpenRead(packagePath))
        {
            var block = new byte[IdentityBlockLength];
            int filled = 0;
            while (filled < block.Length)
            {
                int n = input.Read(block, filled, block.Length - filled);
                if (n <= 0) break;
                filled += n;
            }
            hash.AppendData(block, 0, filled);
        }
        hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(packagePath)));
        return hash.GetHashAndReset();
    }

    /// <summary>
    /// Writes the journal and flushes it to the device. Returns only once the journal is durable —
    /// the whole design rests on the journal outliving a power loss that happens one instruction
    /// into the repair, so the caller may open the package for writing the moment this returns.
    /// </summary>
    internal static void Write(string journalPath, string packagePath, PackageRegions regions,
                               Progress progress)
    {
        long targetLength = new FileInfo(packagePath).Length;
        byte[] identity = ComputeIdentity(packagePath);

        var header = new byte[HeaderLength];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), targetLength);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), regions.CntOffset);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24), regions.Cnt.LongLength);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(32), regions.Si.LongLength);
        identity.CopyTo(header.AsSpan(40));

        long total = regions.Cnt.LongLength + regions.Si.LongLength;
        progress.Detail($"journalling {total:N0} bytes to {journalPath}");

        // Hashed as it is written rather than over a buffered second copy — the payload is ~64 MB
        // and there is no reason to hold it twice.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var journal = new FileStream(journalPath, FileMode.Create, FileAccess.Write,
                                            FileShare.None))
        {
            journal.Write(header);
            hash.AppendData(header);

            long done = 0;
            foreach (var region in new[] { regions.Cnt, regions.Si })
            {
                for (int offset = 0; offset < region.Length; offset += CopyBuffer)
                {
                    int n = Math.Min(CopyBuffer, region.Length - offset);
                    journal.Write(region, offset, n);
                    hash.AppendData(region, offset, n);
                    done += n;
                    progress.Report(done, total);
                }
            }

            journal.Write(hash.GetHashAndReset());
            // To the DEVICE, not merely out of the managed buffer: the caller is about to start
            // mutating the package, and a journal still sitting in the page cache would not
            // survive the very failure it exists for.
            journal.Flush(flushToDisk: true);
        }
    }

    /// <summary>
    /// Reads a journal's header, or null when the file is absent, foreign, truncated or torn.
    /// Never throws: a journal that fails any check is treated as absent, because the alternative
    /// — refusing to run because of an unreadable sidecar — is worse than ignoring it.
    /// </summary>
    internal static RepairJournal? TryRead(string journalPath)
    {
        try
        {
            using var journal = new FileStream(journalPath, FileMode.Open, FileAccess.Read,
                                               FileShare.Read);
            var header = new byte[HeaderLength];
            if (!ReadExactly(journal, header, header.Length)) return null;
            if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return null;

            long targetLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
            long cntOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16));
            long cntLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(24));
            long siLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(32));
            if (targetLength < 0 || cntOffset < 0 || cntLength < 0 || siLength < 0) return null;

            long expected = HeaderLength + cntLength + siLength + DigestLength;
            if (journal.Length != expected) return null;

            // Digest last: it costs a full read of the payload, and a length or magic mismatch
            // already disqualifies the file for free.
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(header);
            var buffer = new byte[CopyBuffer];
            long remaining = cntLength + siLength;
            while (remaining > 0)
            {
                int n = journal.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (n <= 0) return null;
                hash.AppendData(buffer, 0, n);
                remaining -= n;
            }

            var stored = new byte[DigestLength];
            if (!ReadExactly(journal, stored, stored.Length)) return null;
            if (!CryptographicOperations.FixedTimeEquals(stored, hash.GetHashAndReset())) return null;

            return new RepairJournal(targetLength, cntOffset, cntLength, siLength,
                                     header.AsSpan(40, 32).ToArray());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or ArgumentException or NotSupportedException
                                    or OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// Puts the CNT and SI regions back where they came from and truncates the package to its
    /// original length. Refuses — loudly — if the journal was written for a different package.
    /// </summary>
    internal static void Restore(string journalPath, string target, Progress progress)
    {
        var journal = TryRead(journalPath)
            ?? throw new InvalidDataException(
                $"'{journalPath}' is not a readable repair journal, so '{target}' cannot be restored from it");

        var actual = ComputeIdentity(target);
        if (!CryptographicOperations.FixedTimeEquals(actual, journal.Identity))
            throw new InvalidDataException(
                $"the repair journal '{journalPath}' was written for a different package than " +
                $"'{target}' — refusing to restore, because writing one package's regions over " +
                "another would destroy it");

        progress.Stage("restoring the original CNT and SI");
        progress.Detail($"restoring {journal.CntLength:N0} + {journal.SiLength:N0} bytes into {target}");

        using (var source = new FileStream(journalPath, FileMode.Open, FileAccess.Read,
                                           FileShare.Read))
        using (var destination = new FileStream(target, FileMode.Open, FileAccess.Write,
                                                FileShare.None))
        {
            source.Position = HeaderLength;
            destination.Position = journal.CntOffset;   // the SI follows the CNT immediately

            var buffer = new byte[CopyBuffer];
            long total = journal.CntLength + journal.SiLength;
            long remaining = total;
            while (remaining > 0)
            {
                int n = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (n <= 0)
                    throw new EndOfStreamException(
                        $"the repair journal '{journalPath}' ended before its declared payload did");
                destination.Write(buffer, 0, n);
                remaining -= n;
                progress.Report(total - remaining, total);
            }

            // The repair may have shortened or lengthened the package; only the journal knows
            // what it was.
            destination.SetLength(journal.TargetLength);
            destination.Flush(flushToDisk: true);
        }
    }

    private static bool ReadExactly(Stream stream, byte[] buffer, int count)
    {
        int filled = 0;
        while (filled < count)
        {
            int n = stream.Read(buffer, filled, count - filled);
            if (n <= 0) return false;
            filled += n;
        }
        return true;
    }
}

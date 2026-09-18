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
/// device <em>before</em> the package is opened for writing, and deleting it is the repair's last
/// act — so a journal found on disk means the write was interrupted, OR that it completed and the
/// deletion itself failed. Those two are told apart by the file's length; see
/// <see cref="RepairPlayGoCommand.RecoverIfNeeded"/>.
/// </para>
///
/// <para>
/// The journal can be placed anywhere (<c>--work-dir</c>), which is a problem of its own: a later
/// run computing the path from ITS flags would look in the wrong place, find nothing, and report a
/// half-written package as healthy. <see cref="MarkerPathFor"/> closes that — a tiny marker file
/// always beside the package, recording where the journal went.
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

    /// <summary>
    /// The in-progress marker's suffix. Always beside the package, never in <c>--work-dir</c>.
    /// </summary>
    internal const string MarkerSuffix = ".repair-playgo.inprogress";

    /// <summary>
    /// A RETAINED backup's suffix. Same on-disk format as the journal — it IS the journal, renamed
    /// rather than deleted once the repair commits — but a different suffix, and that difference is
    /// load-bearing. <see cref="RepairPlayGoCommand.RecoverIfNeeded"/> treats the PRESENCE of a
    /// journal as "a repair was interrupted", and settles which side of the write a crash fell on
    /// by the file's length. A retained journal reads as "the repair completed" and is deleted on
    /// the very next run; worse, an SI that happened to rebuild to exactly the original length
    /// would read as "interrupted" and roll a good repair back. Keeping the backup out of the
    /// journal's namespace is what makes it safe to leave on disk indefinitely.
    /// </summary>
    internal const string BackupSuffix = ".repair-playgo.backup";

    private static ReadOnlySpan<byte> Magic => "FPKGJRN1"u8;

    private const int HeaderLength = 72;
    private const int DigestLength = 32;
    private const int IdentityBlockLength = 65536;
    private const int CopyBuffer = 81920;

    /// <summary>
    /// Where the journal for <paramref name="target"/> lives.
    ///
    /// <para>
    /// By default: beside the package, named after it — the directory already pairs the two, and a
    /// stray journal is immediately recognisable.
    /// </para>
    ///
    /// <para>
    /// Under <paramref name="workDir"/>, every package's journal lands in one shared directory, so
    /// the name alone is not unique: two packages both called <c>game.pkg</c> in different
    /// directories would collide, and the second repair would silently overwrite the first's
    /// journal — losing the first's recovery data outright if it were interrupted. The name
    /// therefore carries a short digest of the target's full path to keep them apart.
    /// </para>
    /// </summary>
    internal static string PathFor(string target, string? workDir)
    {
        if (string.IsNullOrEmpty(workDir))
            return Path.Combine(Path.GetDirectoryName(target) ?? ".",
                                Path.GetFileName(target) + Suffix);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(target)));
        return Path.Combine(workDir,
                            $"{Path.GetFileName(target)}.{Convert.ToHexString(digest)[..16].ToLowerInvariant()}{Suffix}");
    }

    /// <summary>
    /// Where a retained backup for <paramref name="target"/> lives: always beside the package, like
    /// the marker and unlike the journal. A backup outlives the run that made it, so it must not
    /// depend on that run's <c>--work-dir</c> still being passed — or still existing.
    ///
    /// <para>
    /// The backup is bound to this exact path: <see cref="ComputeIdentity"/> mixes in the target's
    /// full path, so moving or renaming the package makes its backup inapplicable. That is the safe
    /// direction to fail, but it does mean a package being bisected has to stay where it is.
    /// </para>
    /// </summary>
    internal static string BackupPathFor(string target) =>
        Path.Combine(Path.GetDirectoryName(target) ?? ".",
                     Path.GetFileName(target) + BackupSuffix);

    /// <summary>
    /// The in-progress marker for <paramref name="target"/>: <c>&lt;package&gt;.repair-playgo.inprogress</c>,
    /// ALWAYS beside the package. It takes no <c>workDir</c> on purpose — a marker that moved with
    /// the journal would be exactly as findable as the journal, which is to say not findable at all.
    /// </summary>
    internal static string MarkerPathFor(string target) =>
        Path.Combine(Path.GetDirectoryName(target) ?? ".",
                     Path.GetFileName(target) + MarkerSuffix);

    /// <summary>
    /// Records, beside the package, where this run's journal lives, and flushes it to the device
    /// before returning — the caller writes the journal next, and a marker still sitting in the page
    /// cache would not survive the failure the whole mechanism exists for.
    ///
    /// <para>
    /// WHY IT EXISTS. Without it, recovery can only recompute the journal's path from the flags of
    /// the run that happens to be recovering. Any of the ordinary ways those differ — the journal
    /// was under <c>--work-dir /tmp</c> and the reboot cleared <c>/tmp</c>; the user forgot the flag;
    /// the user passed a different one — makes the journal invisible. Recovery then finds nothing,
    /// the detector reads entries 4097/8209 out of the already-REPAIRED CNT, sees nothing wrong, and
    /// the command prints "Nothing to repair" and exits 0 on a package whose SI is stale. That is
    /// silent, undetectable data loss, and it is the failure the journal was supposed to prevent.
    /// The marker makes the mid-repair state impossible to miss even when the recovery data is gone.
    /// </para>
    /// </summary>
    internal static void WriteMarker(string markerPath, string journalPath)
    {
        using var marker = new FileStream(markerPath, FileMode.Create, FileAccess.Write,
                                          FileShare.None);
        marker.Write(Encoding.UTF8.GetBytes(Path.GetFullPath(journalPath)));
        marker.Flush(flushToDisk: true);
    }

    /// <summary>
    /// The journal path a marker records, or null when there is no marker. An unreadable or empty
    /// marker reads as absent: it cannot name a journal, so it cannot redirect recovery — and
    /// <see cref="RepairPlayGoCommand.RecoverIfNeeded"/>'s own <c>File.Exists</c> check still
    /// refuses the run, so nothing is quietly waved through.
    /// </summary>
    internal static string? ReadMarker(string markerPath)
    {
        try
        {
            string recorded = File.ReadAllText(markerPath).Trim();
            return recorded.Length == 0 ? null : recorded;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// SHA-256 of file[0, 65536) — the FIH block — mixed with the package's full path.
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
    /// The path is mixed in because content alone cannot distinguish a package from its own
    /// repaired form: sharing the payload is the feature's central property — the acceptance
    /// oracle and the test package are byte-identical for the first 596 MB, and
    /// <c>OracleTests</c> asserts as much. That is not a flaw in the region choice; it is
    /// inherent, since any region an interrupted repair leaves intact is also left intact by a
    /// completed one. The path is the only discriminator that survives a partial write, and it is
    /// exactly right for this job — an in-place repair recovers the same path it crashed on. The
    /// full path, not the file name: <see cref="PathFor"/> names journals after the package, so
    /// two packages both called <c>game.pkg</c> in different directories would otherwise
    /// contribute the same identity. The cost is that a package renamed after a crash has an
    /// inapplicable journal; that is the safe direction to fail. Note too that
    /// <see cref="Path.GetFullPath(string)"/> normalises but does not resolve symlinks, so
    /// <c>/tmp/x.pkg</c> and <c>/private/tmp/x.pkg</c> refuse each other even though they are one
    /// file — the safe direction again, but not something a reader should have to discover.
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
        hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFullPath(packagePath)));
        return hash.GetHashAndReset();
    }

    /// <summary>
    /// Writes the journal and flushes it to the device. Returns only once the journal is durable —
    /// the whole design rests on the journal outliving a power loss that happens one instruction
    /// into the repair, so the caller may open the package for writing the moment this returns.
    ///
    /// <para>
    /// "Durable" here means <c>fsync</c>, which is what <c>Flush(flushToDisk: true)</c> maps to on
    /// macOS — not <c>F_FULLFSYNC</c>, and the containing directory is never synced. So a returned
    /// <c>Write</c> means the journal survives a process or kernel crash, not that it is certain to
    /// survive a power cut. Closing that last gap belongs to a layer below this one.
    /// </para>
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
        using (var journal = CreateNew(journalPath))
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
    /// The one wording for "there is already a journal here", shared by this class and by
    /// <see cref="InPlaceWriter"/> so a caller cannot meet two different explanations of the same
    /// condition.
    /// </summary>
    internal static string AlreadyPresent(string journalPath) =>
        $"a repair journal is already present at '{journalPath}'; an interrupted repair must be " +
        "recovered (or the journal deliberately removed) before another in-place repair can start";

    /// <summary>
    /// <see cref="FileMode.CreateNew"/>, never <see cref="FileMode.Create"/>: an existing journal is
    /// an interrupted repair's ONLY copy of the original CNT and SI, and <c>Create</c> truncates —
    /// so a re-run's very first act would be to destroy the data that would have undone it. The
    /// refusal lives here, at the syscall, rather than only in the callers, so no future caller can
    /// reintroduce the truncation by forgetting to check.
    /// </summary>
    private static FileStream CreateNew(string journalPath)
    {
        try
        {
            return new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        // CreateNew reports an existing file as a plain IOException, which is indistinguishable by
        // type from a full disk or a dead device — hence the File.Exists re-check rather than a
        // blanket translation that would mislabel a genuine I/O failure as a stale journal.
        catch (IOException) when (File.Exists(journalPath))
        {
            throw new InvalidOperationException(AlreadyPresent(journalPath));
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

            // The regions must fit inside the package the journal claims to describe. Restore seeks
            // to CntOffset and writes CntLength + SiLength bytes, then truncates to TargetLength, so
            // a journal declaring more than fits would write past the length it then cuts back to —
            // silently discarding part of its own recovery data. Subtraction rather than addition:
            // the lengths are bounded by the file's real size above, but CntOffset is not, and
            // cntOffset + cntLength + siLength could overflow.
            if (cntOffset > targetLength - (cntLength + siLength)) return null;

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

        // Checked before the target is opened for writing, let alone written to: a refusal must
        // leave the file exactly as it was found.
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

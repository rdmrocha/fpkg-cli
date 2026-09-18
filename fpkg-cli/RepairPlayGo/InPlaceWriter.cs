namespace Fpkg.Cli.RepairPlayGo;

/// <summary>
/// Writes a repair into the package itself: the repaired CNT back into its own footprint, the
/// rebuilt SI appended, the file truncated to the new end. Nothing before
/// <see cref="PackageRegions.CntOffset"/> is ever read for writing, so the ~600 MB outer PFS is
/// neither copied nor rewritten. The only new allocation is the journal — ~64 MB on the test
/// package, the size of the CNT and SI it captures — not a second copy of the package.
/// </summary>
/// <remarks>
/// <para>
/// This is only sound because the CNT region's length is invariant: <see cref="CntRepair"/>
/// refuses any relayout that changes <c>body_size</c>, and refuses a CNT region carrying padding
/// past its body, so the repaired CNT is always exactly as long as the one it replaces. Only the
/// trailing SI changes length, and it is the last thing in the file.
/// </para>
/// <para>
/// The window in which the package is neither the original nor the repair is covered by the
/// journal: it holds the original CNT and SI, is durable before the first byte is written, and is
/// deleted only once the repair has been committed to the device. A marker file beside the package
/// records WHERE that journal went, so a later run can find it whatever flags it is given — see
/// <see cref="RepairJournal.WriteMarker"/>.
/// </para>
/// </remarks>
internal static class InPlaceWriter
{
    private const int CopyBuffer = 1 << 20;

    /// <summary>
    /// Runs the five-step sequence: journal, splice the CNT, rebuild the SI over the now-repaired
    /// file, append and truncate, drop the journal. The order is the design, not a preference.
    /// </summary>
    /// <param name="regions">The regions as loaded from <paramref name="target"/> before the repair.</param>
    /// <param name="repaired">The repaired CNT and the regenerated <c>playgo-chunk.dat</c>.</param>
    /// <param name="contentId">Content id; it names the <c>playgo-chunk.crc</c> member in the SI.</param>
    /// <param name="target">The package to mutate, in place.</param>
    /// <param name="journalPath">Where the recovery journal lives for the duration of the write.</param>
    /// <param name="progress">Four stages: journal, CNT, CRC/SI rebuild, append.</param>
    /// <param name="faultAfterCntWrite">
    /// Test seam, and nothing else: invoked once the CNT has been spliced and committed but before
    /// the SI is rebuilt — the exact window in which the package is neither the original nor the
    /// repair. Recoverability from that window is the property the journal exists for, and there is
    /// no external way to provoke it (it needs an I/O error, a full disk or a kill), so the seam is
    /// how <c>InPlaceWriterTests</c> proves the journal survives instead of asserting it by reading
    /// the code. Production callers leave it null.
    /// </param>
    internal static void Write(PackageRegions regions, CntRepairResult repaired,
                               string contentId, string target, string journalPath,
                               Progress progress, Action? faultAfterCntWrite = null)
    {
        // RepairJournal.Write opens with FileMode.CreateNew and refuses this on its own, so an
        // existing journal can never be truncated even if this check were dropped. It is kept
        // because it is cheap and it comes FIRST: refusing here costs nothing, whereas the refusal
        // inside Write arrives only after ~64 MB of journalling has been staged, and the caller
        // gets the same actionable message either way.
        if (File.Exists(journalPath))
            throw new InvalidOperationException(RepairJournal.AlreadyPresent(journalPath));

        // The load-bearing invariant. If these ever differ, writing in place would either shift the
        // SI over live CNT bytes or leave a hole, and no amount of padding makes that correct.
        if (repaired.Cnt.LongLength != regions.Cnt.LongLength)
            throw new InvalidOperationException(
                $"the repaired CNT is {repaired.Cnt.LongLength:N0} bytes but the region it must occupy is " +
                $"{regions.Cnt.LongLength:N0}; in-place writing requires them to be equal");

        // 1. The journal is durable before a single byte of the package is touched — and the marker
        //    beside the package, recording where that journal went, is durable before the journal.
        //    The ordering matters in that direction only: a marker naming a journal that does not
        //    exist yet refuses the next run loudly (correct — nothing has been written), whereas a
        //    journal with no marker is a journal a later run may fail to find.
        progress.Stage("journalling the original CNT and SI");
        string markerPath = RepairJournal.MarkerPathFor(target);
        RepairJournal.WriteMarker(markerPath, journalPath);
        RepairJournal.Write(journalPath, target, regions, progress);

        // The `catch` announces and RETHROWS, changing nothing else: everything from here on runs
        // with the package mid-repair, so a refusal or an I/O failure below leaves a file that is
        // neither the original nor the repair. Without this the user is told only, say, `error: the
        // SI segment is missing '...'` — a message that reads like "nothing happened" and invites
        // them to delete the "stray" sidecar that is the package's only way back. There is
        // deliberately no `finally` and nothing is deleted here: the journal MUST outlive a failure.
        try
        {
            using (var file = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                // 2. The repaired CNT goes back into its own footprint, byte for byte the same length.
                progress.Stage("writing the repaired CNT in place");
                file.Seek(regions.CntOffset, SeekOrigin.Begin);
                for (long offset = 0; offset < repaired.Cnt.LongLength; offset += CopyBuffer)
                {
                    // The cast cannot overflow: Cnt is a byte[] held whole in memory, so it is
                    // int-indexable by construction. Long arithmetic here only to stay consistent with
                    // the LongLength the guard above compares.
                    int n = (int)Math.Min(CopyBuffer, repaired.Cnt.LongLength - offset);
                    file.Write(repaired.Cnt, checked((int)offset), n);
                    progress.Report(offset + n, repaired.Cnt.LongLength);
                }
                // NOT for the CRC's benefit: FileStream flushes its own write buffer on the Seek and
                // reads that follow against this same handle, so the CRC would see the new CNT either
                // way. This is durability and write ordering — the CNT reaches the device before the
                // SI that describes it, so a crash between them leaves a state the journal can undo.
                // Do not remove it on the grounds that the CRC does not need it.
                file.Flush(flushToDisk: true);
    
                faultAfterCntWrite?.Invoke();
    
                // 3. The target IS the repaired mount image now, so the CRC reads it directly — no
                //    staged copy exists to read instead. BuildChunkCrc reduces from the stream's
                //    current position, so it starts at 0, and restores that position afterwards.
                progress.Stage("recomputing the chunk CRC and rebuilding the SI");
                long siOffset = regions.CntOffset + repaired.Cnt.LongLength;
                file.Seek(0, SeekOrigin.Begin);
                // Wrapped for the duration of the Rebuild call only. This is the longest step of the
                // whole run (~597 MB of CRC reduction) and without the wrapper it printed a stage banner
                // and then nothing at all. It cannot replace `file` wholesale: ReadingProgressStream is
                // read-only by design (CanWrite => false) and step 4 writes through this same handle.
                byte[] newSi;
                using (var reading = new ReadingProgressStream(file, progress))
                    newSi = SiRepair.Rebuild(regions.Si, contentId, repaired.NewChunkDat,
                                             reading, siOffset, progress);
    
                EnsureSiDoesNotGrow(newSi.LongLength, regions.Si.LongLength);
    
                // 4. The SI is the last thing in the file, so it can simply be overwritten and the file
                //    cut to the new end — the repaired SI is slightly shorter than the original, which
                //    the guard above turns from an observation into a requirement.
                progress.Stage("appending the rebuilt SI");
                file.Seek(siOffset, SeekOrigin.Begin);
                file.Write(newSi, 0, newSi.Length);
                file.SetLength(siOffset + newSi.LongLength);
                file.Flush(flushToDisk: true);
            }
        }
        catch
        {
            Console.Error.WriteLine(
                $"warning: '{target}' has been left MID-REPAIR — the repaired CNT is on disk but " +
                "the SI that describes it is not, so the package is not in a usable state.");
            Console.Error.WriteLine(
                $"warning: the repair journal at '{journalPath}' can put it back. Re-running " +
                "`fpkg repair-playgo` on this package restores it from that journal and stops. Do " +
                "NOT delete the journal or the marker beside the package until you have.");
            throw;
        }

        // 5. Only now, with the repair committed to the device, do the journal and the marker that
        //    points at it stop being needed. The journal goes first: a marker naming a journal that
        //    is already gone is the one combination recovery must refuse loudly, and it would be
        //    plain wrong here.
        File.Delete(journalPath);
        File.Delete(markerPath);
    }

    /// <summary>
    /// The SECOND load-bearing invariant, and the less obvious one: the rebuilt SI must not be
    /// LONGER than the original. Measured on the test package it shrinks, 665,774 -> 664,414, and
    /// the whole recovery story is built on that.
    ///
    /// <para>
    /// Not a matter of layout — <c>SetLength</c> would happily grow the file — but of CRASH
    /// RECOVERY. <see cref="RepairPlayGoCommand.RecoverIfNeeded"/> decides whether an interrupted
    /// repair is rolled back by comparing the file's length against the journal's
    /// <c>TargetLength</c>: equal means "steps 2-4 never finished, restore", different means "the
    /// repair ran through, discard the stale journal". A GROWING SI breaks that. The SI write would
    /// push the file past <c>TargetLength</c> before <c>SetLength</c> is ever reached, so a crash in
    /// the middle of it leaves a half-written package whose length already differs — which recovery
    /// reads as a completed repair. It would then delete the journal and leave the user a broken
    /// package with no way back.
    /// </para>
    /// <para>
    /// Refusing is the same answer the CNT footprint guard gives, and for the same reason: an
    /// invariant the design rests on is enforced, not assumed. Do NOT "fix" this by calling
    /// <c>SetLength</c> before the write — growing the file first only reopens the same window in a
    /// different shape, with the package still unrecoverable in the middle of it.
    /// </para>
    /// <para>
    /// Extracted from <see cref="Write"/> and taking two plain integers so that it can be pinned by
    /// a test. Inline, its deletion left the entire suite green: reaching it needs a package whose
    /// SI grows, and there isn't one.
    /// </para>
    /// </summary>
    internal static void EnsureSiDoesNotGrow(long rebuilt, long original)
    {
        if (rebuilt > original)
            throw new InvalidOperationException(
                $"the rebuilt SI is {rebuilt:N0} bytes but the original is {original:N0}; " +
                "in-place writing requires that it not grow, because crash recovery tells an " +
                "interrupted repair from a completed one by the file's length, and an SI that grows " +
                "past the original length mid-write would be mistaken for a completed repair and " +
                "its journal discarded.");
    }
}

namespace Fpkg.Cli.RepairPlayGo;

/// <summary>
/// Writes a repair into the package itself: the repaired CNT back into its own footprint, the
/// rebuilt SI appended, the file truncated to the new end. Nothing before
/// <see cref="PackageRegions.CntOffset"/> is ever read for writing, so the ~600 MB outer PFS is
/// neither copied nor rewritten and the repair needs no free space beside the target.
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
/// deleted only once the repair has been committed to the device.
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
    internal static void Write(PackageRegions regions, CntRepairResult repaired,
                               string contentId, string target, string journalPath,
                               Progress progress)
    {
        // A journal already on disk is an interrupted repair's ONLY copy of the original CNT and
        // SI, and RepairJournal.Write opens with FileMode.Create — so a re-run's first act would
        // be to destroy the very data needed to undo the half-finished one. Refused here, at the
        // point of danger, rather than trusting every caller to have checked.
        if (File.Exists(journalPath))
            throw new InvalidOperationException(
                $"a repair journal is already present at '{journalPath}'; an interrupted repair must be " +
                "recovered (or the journal deliberately removed) before another in-place repair can start");

        // The load-bearing invariant. If these ever differ, writing in place would either shift the
        // SI over live CNT bytes or leave a hole, and no amount of padding makes that correct.
        if (repaired.Cnt.LongLength != regions.Cnt.LongLength)
            throw new InvalidOperationException(
                $"the repaired CNT is {repaired.Cnt.LongLength:N0} bytes but the region it must occupy is " +
                $"{regions.Cnt.LongLength:N0}; in-place writing requires them to be equal");

        // 1. The journal is durable before a single byte of the package is touched.
        progress.Stage("journalling the original CNT and SI");
        RepairJournal.Write(journalPath, target, regions, progress);

        using (var file = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // 2. The repaired CNT goes back into its own footprint, byte for byte the same length.
            progress.Stage("writing the repaired CNT in place");
            file.Seek(regions.CntOffset, SeekOrigin.Begin);
            for (int offset = 0; offset < repaired.Cnt.Length; offset += CopyBuffer)
            {
                int n = Math.Min(CopyBuffer, repaired.Cnt.Length - offset);
                file.Write(repaired.Cnt, offset, n);
                progress.Report(offset + n, repaired.Cnt.LongLength);
            }
            // To the DEVICE before the CRC pass reads it back: the CRC now reduces over THIS file,
            // and a stale managed buffer here would silently produce a wrong CRC table.
            file.Flush(flushToDisk: true);

            // 3. The target IS the repaired mount image now, so the CRC reads it directly — no
            //    staged copy exists to read instead. BuildChunkCrc reduces from the stream's
            //    current position, so it starts at 0, and restores that position afterwards.
            progress.Stage("recomputing the chunk CRC and rebuilding the SI");
            long siOffset = regions.CntOffset + repaired.Cnt.LongLength;
            file.Seek(0, SeekOrigin.Begin);
            byte[] newSi = SiRepair.Rebuild(regions.Si, contentId, repaired.NewChunkDat,
                                            file, siOffset, progress);

            // 4. The SI is the last thing in the file, so it can simply be overwritten and the file
            //    cut to the new end — the repaired SI is usually slightly shorter than the original.
            progress.Stage("appending the rebuilt SI");
            file.Seek(siOffset, SeekOrigin.Begin);
            file.Write(newSi, 0, newSi.Length);
            file.SetLength(siOffset + newSi.LongLength);
            file.Flush(flushToDisk: true);
        }

        // 5. Only now, with the repair committed to the device, does the journal stop being needed.
        File.Delete(journalPath);
    }
}

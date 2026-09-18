namespace Fpkg.Cli.RepairPlayGo;

/// <summary>
/// The outcome of a CNT repair: the resealed region, the regenerated <c>playgo-chunk.dat</c>
/// (which a later step also has to splice into the outer PFS), and the two layout numbers the
/// repair was gated on.
/// </summary>
/// <param name="Cnt">The resealed CNT region.</param>
/// <param name="NewChunkDat">The regenerated entry 4097 payload.</param>
/// <param name="SlackBefore">
/// Unused bytes between the end of the last physical entry and the end of the body, measured on
/// the ORIGINAL layout. Negative if the original layout already overran its declared body.
/// </param>
/// <param name="NetDelta">
/// Signed sum of the three PlayGo entries' size changes. Negative means the body shrinks, which
/// is the normal case: the repaired chunk.dat is considerably smaller than the broken one.
/// </param>
internal sealed record CntRepairResult(
    byte[] Cnt, byte[] NewChunkDat, long SlackBefore, long NetDelta);

/// <summary>
/// Assembles the whole CNT-side repair: recover the PlayGo layout from the broken
/// <c>playgo-chunk.dat</c>, regenerate the three PlayGo entries, substitute their payloads and
/// reseal the container.
/// </summary>
internal static class CntRepair
{
    private const uint ChunkDatId  = 4097;   // playgo-chunk.dat
    private const uint FicmId      = 8209;   // playgo-ficm.dat
    private const uint ScenarioId  = 12288;  // playgo-scenario.json

    internal static CntRepairResult Repair(byte[] cnt, string contentId, string passcode)
    {
        var table = CntEntryTable.Parse(cnt, passcode);

        // Slack is measured on the ORIGINAL layout, from the last entry in PHYSICAL order — which
        // is not the last entry by id. The entry's own size is used unaligned: nothing follows it,
        // so its 16-byte tail padding is part of the free space, not part of the entry.
        var last = table.Physical[^1];
        ulong bodyOffset = CntHeader.U64(cnt, CntHeader.BodyOffset);
        ulong bodySize   = CntHeader.U64(cnt, CntHeader.BodySize);
        long bodyEnd     = (long)(bodyOffset + bodySize);
        long usedEnd     = (long)last.DataOffset + last.DataSize;
        long slackBefore = bodyEnd - usedEnd;

        var chunkDat = table[ChunkDatId];
        var ficm     = table[FicmId];
        var scenario = table[ScenarioId];

        var rebuilt = PlayGoEntries.Build(PlayGoRecovery.From(chunkDat.Payload), ficm.Payload);

        long netDelta =
            (rebuilt.ChunkDat.Length     - (long)chunkDat.Payload.Length) +
            (rebuilt.Ficm.Length         - (long)ficm.Payload.Length) +
            (rebuilt.ScenarioJson.Length - (long)scenario.Payload.Length);

        // Checked BEFORE Seal: Seal has its own, coarser body_size guard, and letting that one fire
        // first would report the symptom (a body_size change) instead of the cause (no room). A
        // negative delta is the normal case and must not trip this.
        if (netDelta > slackBefore)
            throw new InvalidOperationException(
                $"The repaired PlayGo entries change the CNT body's size by {netDelta:+#;-#;0} " +
                $"bytes, but the body has only {slackBefore} bytes of slack at its end. Growing the " +
                "body would mean bumping body_size and moving the container end, which also moves " +
                "cnt_region_size, package_size, mount_image_size, promote_size and " +
                "pfs_image_offset; that fallback is specified but not implemented.");

        // Only the payloads are assigned: CntReseal.Seal re-derives every DataSize from
        // Payload.Length and every DataOffset from the layout walk.
        chunkDat.Payload = rebuilt.ChunkDat;
        ficm.Payload     = rebuilt.Ficm;
        scenario.Payload = rebuilt.ScenarioJson;

        var sealedCnt = CntReseal.Seal(cnt, table.Physical, contentId, passcode);
        return new CntRepairResult(sealedCnt, rebuilt.ChunkDat, slackBefore, netDelta);
    }
}

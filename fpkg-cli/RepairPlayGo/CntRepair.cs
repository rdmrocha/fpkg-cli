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
        // is not the last entry by id. The entry's own size is used UNALIGNED, deliberately: its
        // 16-byte alignment tail counts as slack precisely because nothing follows it, so those
        // padding bytes are free space rather than part of the entry. Aligning here would
        // under-report the slack by 3 bytes on this package. Do not "fix" it back.
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

        // ORDERING IS LOAD-BEARING: this is checked BEFORE Seal. Seal has its own, coarser
        // body_size guard that would also fire on an overrun, and letting it go first would report
        // the symptom (body_size changed) instead of the cause (no room at the end of the body).
        // The two messages are distinguishable — Seal's does not contain the word "slack" — so the
        // guard test asserting on "slack" fails loudly if this check is ever moved after Seal,
        // rather than passing on the wrong exception.
        if (ExceedsSlack(netDelta, slackBefore))
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

    /// <summary>
    /// The repair's whole fit decision, extracted so it can be tested over cases no real package
    /// produces — every package on disk has a NEGATIVE <paramref name="netDelta"/>, so the growth
    /// case this guard exists for is unreachable through <see cref="Repair"/>.
    /// <para>
    /// Both arguments are SIGNED and both can legitimately be negative: a shrinking body gives a
    /// negative delta, and a package whose last entry already overruns its declared body gives a
    /// negative slack. A comparison done in unsigned arithmetic would invert on either.
    /// </para>
    /// <para>
    /// The boundary is INCLUSIVE — a delta exactly equal to the slack fits and is allowed, because
    /// the slack is the count of bytes actually available, so consuming all of them lands the body
    /// end exactly on the declared end and overruns nothing.
    /// </para>
    /// </summary>
    internal static bool ExceedsSlack(long netDelta, long slackBefore) => netDelta > slackBefore;
}

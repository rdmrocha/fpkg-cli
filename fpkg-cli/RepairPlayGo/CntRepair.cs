using System.Text;

namespace Fpkg.Cli.RepairPlayGo;

/// <summary>
/// The outcome of a CNT repair: the resealed region, the regenerated <c>playgo-chunk.dat</c>
/// (which a later step also has to splice into the outer PFS), and the two layout numbers the
/// dry-run output reports.
/// </summary>
/// <param name="Cnt">The resealed CNT region.</param>
/// <param name="NewChunkDat">The regenerated entry 4097 payload.</param>
/// <param name="SlackBefore">
/// DIAGNOSTIC ONLY, not the gate. Unused bytes between the end of the last physical entry and the
/// end of the body, measured on the ORIGINAL layout. Negative if the original layout already
/// overran its declared body.
/// </param>
/// <param name="NetDelta">
/// DIAGNOSTIC ONLY, not the gate. Signed sum of the three PlayGo entries' RAW size changes.
/// Negative means the body shrinks, which is the normal case: the repaired chunk.dat is
/// considerably smaller than the broken one. It is a raw length sum, so it under-counts the real
/// movement of the body end by up to 15 bytes per entry whose length is not a multiple of 16 —
/// which is exactly why <see cref="CntRepair.PredictBodySize"/>, not this number, is the gate.
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
    private const uint EntryKeysId = 16;
    private const uint MetasId     = 256;

    /// <summary>Mirrors <c>CntReseal</c>'s own constant: 0xB80 selects the 64-KiB body rounding.</summary>
    private const uint PublisherEntryKeysSize = 0xB80;

    /// <summary>Entry alignment inside the body, as <c>CntReseal</c>'s layout walk uses it.</summary>
    private const ulong EntryAlignment = 16;

    internal static CntRepairResult Repair(byte[] cnt, string contentId, string passcode)
    {
        ulong bodyOffset = CntHeader.U64(cnt, CntHeader.BodyOffset);
        ulong bodySize   = CntHeader.U64(cnt, CntHeader.BodySize);

        // PRECONDITION, checked first: the CNT region ends exactly where its body ends.
        // CntReseal.Seal returns `new byte[bodyOffset + bodySize]` — it reconstructs the region
        // from the body alone and structurally cannot represent trailing padding, so padding would
        // be dropped silently and the region would come back shorter than it went in. The SI's CRC
        // length rule also assumes region-end and body-end coincide.
        //
        // Safe to REQUIRE rather than handle: the 0.6.9 builder's LayOutEntries sets
        // cnt_region_size = pfs_image_offset, and pfs_image_offset = body_offset + body_size, so
        // any package these tools produced satisfies this by construction. That is the rationale —
        // do not "relax" this check without first giving Seal a way to carry padding through, which
        // is a real feature with no test material behind it. Refusing is the right answer for a
        // rewrite this hard to undo.
        if ((ulong)cnt.Length != bodyOffset + bodySize)
            throw new InvalidOperationException(
                $"The CNT region is {cnt.Length} bytes but its body ends at " +
                $"{bodyOffset + bodySize} (body_offset 0x{bodyOffset:X} + body_size 0x{bodySize:X}), " +
                $"so it carries {(long)cnt.Length - (long)(bodyOffset + bodySize)} bytes past the " +
                "end of the body. repair-playgo cannot preserve that padding: the reseal " +
                "reconstructs the region from the body alone, and the SI's CRC length rule assumes " +
                "the region end and the body end coincide.");

        // CntEntryTable.Parse derives the content id from the header to DECRYPT with, while the
        // caller's value is what Seal RE-ENCRYPTS with. A mismatch would silently produce a
        // container decrypted under one key and sealed under another. The parameter is kept rather
        // than dropped — Seal's signature takes one, and an explicit argument documents at the call
        // site which id the output is bound to — but it is now verified against the header instead
        // of trusted.
        string headerContentId = Encoding.ASCII
            .GetString(cnt, CntHeader.ContentId, 36).TrimEnd('\0');
        if (!string.Equals(contentId, headerContentId, StringComparison.Ordinal))
            throw new ArgumentException(
                $"The content id passed in ('{contentId}') is not the one in the CNT header " +
                $"('{headerContentId}'). The header's id is what the entries were decrypted with, " +
                "so sealing under a different one would produce an unreadable container.",
                nameof(contentId));

        var table = CntEntryTable.Parse(cnt, passcode);

        // Slack is measured on the ORIGINAL layout, from the last entry in PHYSICAL order — which
        // is not the last entry by id. The entry's own size is used UNALIGNED, deliberately: its
        // 16-byte alignment tail counts as slack precisely because nothing follows it, so those
        // padding bytes are free space rather than part of the entry. Aligning here would
        // under-report the slack by 3 bytes on this package. Do not "fix" it back.
        var last = table.Physical[^1];
        long slackBefore = (long)(bodyOffset + bodySize) - ((long)last.DataOffset + last.DataSize);

        var chunkDat = table[ChunkDatId];
        var ficm     = table[FicmId];
        var scenario = table[ScenarioId];

        var rebuilt = PlayGoEntries.Build(PlayGoRecovery.From(chunkDat.Payload), ficm.Payload);

        long netDelta =
            (rebuilt.ChunkDat.Length     - (long)chunkDat.Payload.Length) +
            (rebuilt.Ficm.Length         - (long)ficm.Payload.Length) +
            (rebuilt.ScenarioJson.Length - (long)scenario.Payload.Length);

        // ---- The gate ---------------------------------------------------------------------
        // Not "does the growth fit in the slack": that proxy is both inexact (it sums RAW lengths
        // while the body end advances on 16-ALIGNED ones — equal here only because 6736 and 5376
        // happen to be multiples of 16) and one-sided (slack bounds growth only, so a large enough
        // SHRINK sails past it and then dies inside Seal). The exact question is the one Seal
        // actually asks: does the relayout leave body_size unchanged? Simulating Seal's own walk
        // answers it exactly and in both directions.
        var newLengths = table.Physical
            .Select(e => e.Id switch
            {
                ChunkDatId => (uint)rebuilt.ChunkDat.Length,
                FicmId     => (uint)rebuilt.Ficm.Length,
                ScenarioId => (uint)rebuilt.ScenarioJson.Length,
                // METAS' payload IS the table it describes, so Seal sizes it from the entry count
                // rather than from the stale payload. Mirrored here for the same reason.
                MetasId    => (uint)(table.Physical.Count * 32),
                _          => (uint)e.Payload.Length,
            })
            .ToList();

        // Derived, never hard-coded: Seal picks the body rounding off ENTRY_KEYS' size, and this
        // prediction is worthless if the two ever disagree about which rounding applies.
        ulong bodyAlignment =
            table[EntryKeysId].Payload.Length == PublisherEntryKeysSize ? 0x10000UL : 0x80000UL;
        ulong newBodySize = PredictBodySize(bodyOffset, bodyAlignment, newLengths);

        // ORDERING IS LOAD-BEARING: this is checked BEFORE Seal. Seal asks the same question at the
        // end of its own relayout and throws too, but its message reports only the symptom
        // ("the resealed body_size changed from X to Y") with no account of why. The two messages
        // are distinguishable — Seal's contains neither "slack" nor "PlayGo" — so the guard test,
        // which asserts on "slack", fails loudly if this check is ever moved after Seal rather than
        // passing on the wrong exception.
        if (newBodySize != bodySize)
            throw new InvalidOperationException(
                $"The repaired PlayGo entries would move the CNT body end past a rounding step: " +
                $"body_size would {(newBodySize > bodySize ? "grow" : "shrink")} from 0x{bodySize:X} " +
                $"to 0x{newBodySize:X} (raw net delta {netDelta:+#;-#;0} bytes, slack at the end of " +
                $"the original body {slackBefore} bytes). Changing body_size moves the container " +
                "end, which also requires cnt_region_size, package_size, mount_image_size, " +
                "promote_size and pfs_image_offset to be recomputed; that body_size-bump path is " +
                "specified but not implemented.");

        // Only the payloads are assigned: CntReseal.Seal re-derives every DataSize from
        // Payload.Length and every DataOffset from the layout walk.
        chunkDat.Payload = rebuilt.ChunkDat;
        ficm.Payload     = rebuilt.Ficm;
        scenario.Payload = rebuilt.ScenarioJson;

        var sealedCnt = CntReseal.Seal(cnt, table.Physical, contentId, passcode);
        return new CntRepairResult(sealedCnt, rebuilt.ChunkDat, slackBefore, netDelta);
    }

    /// <summary>
    /// Replays <c>CntReseal.Seal</c>'s layout walk over the payload lengths a reseal WOULD see, and
    /// returns the <c>body_size</c> it would then write. Extracted and pure so the accept and
    /// refuse cases can be tested directly: no package on disk can reach either failure, because
    /// every one of them shrinks by a comfortable, in-step amount.
    /// <para>
    /// The walk is a deliberate mirror of Seal's, including that the LAST entry's length is aligned
    /// too. Seal's <c>num</c> is aligned on every iteration, so the value it rounds to
    /// <paramref name="bodyAlignment"/> is the aligned end; predicting from the unaligned end would
    /// disagree with it whenever the two straddle a rounding step. (The separately reported
    /// <c>SlackBefore</c> uses the unaligned end for a different question — how much free space
    /// there is — and the two must not be conflated.)
    /// </para>
    /// </summary>
    internal static ulong PredictBodySize(
        ulong bodyOffset, ulong bodyAlignment, IReadOnlyList<uint> physicalLengths)
    {
        ulong num = bodyOffset;
        foreach (uint length in physicalLengths)
            num = Align(num + length, EntryAlignment);
        return Align(num, bodyAlignment) - bodyOffset;
    }

    private static ulong Align(ulong value, ulong alignment) =>
        (value + alignment - 1) / alignment * alignment;
}

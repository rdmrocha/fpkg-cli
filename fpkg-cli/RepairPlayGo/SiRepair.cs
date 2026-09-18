using System.IO.Compression;
using LibProsperoPkg.PKG;
using LibProsperoPkg.PlayGo;

namespace Fpkg.Cli.RepairPlayGo;

/// <summary>
/// Rebuilds the trailing SI segment (the <c>sce_suppl</c> ZIP) from its own members, substituting
/// the regenerated <c>playgo-chunk.dat</c> and recomputing <c>playgo-chunk.crc</c> over the
/// repaired mount image.
/// </summary>
/// <remarks>
/// The container framing is not reimplemented here: <see cref="ProsperoSiArchive.BuildMembers"/>
/// owns the member set and its order, and <see cref="ProsperoSiArchive.WriteZip"/> owns the ZIP
/// framing. This class only decides WHICH bytes go in, which makes the SI fixed point
/// (rebuild-from-own-members == original) a meaningful test of both halves.
/// </remarks>
internal static class SiRepair
{
    internal const string PfsImageXmlPath = "common/etc/pfsimage.xml";
    internal const string NapsMeta18Path  = "common/etc/naps_meta_18.dat";
    internal const string ChunkDatPath    = "common/etc/playgo-chunk.dat";

    /// <summary>
    /// The four <c>naps_meta_3xx</c> member paths. They carry one byte-identical 48-byte payload,
    /// which is why <see cref="ProsperoSiArchive.BuildMembers"/> takes a single blob and emits it
    /// at all four paths. <see cref="Rebuild"/> verifies the identity rather than assuming it.
    /// </summary>
    internal static readonly string[] NapsMeta300Paths =
    [
        "common/etc/naps_meta_300.dat",
        "common/etc/naps_meta_301.dat",
        "common/etc/naps_meta_302.dat",
        "common/etc/naps_meta_308.dat",
    ];

    /// <summary>Reads the SI zip's members so a repaired one can be rebuilt from them.</summary>
    internal static IReadOnlyDictionary<string, byte[]> ReadMembers(byte[] si)
    {
        var members = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var stream = new MemoryStream(si, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            using var content = entry.Open();
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            if (!members.TryAdd(entry.FullName, buffer.ToArray()))
                throw new InvalidDataException(
                    $"the SI segment lists '{entry.FullName}' more than once; refusing to guess which copy is current");
        }
        return members;
    }

    /// <summary>
    /// Every structural condition <see cref="Rebuild"/> needs of the SI's member set, in one place
    /// so it can be asked BEFORE anything is written as well as at the point of use.
    ///
    /// <para>
    /// The separation is the point. Under <c>--in-place</c>, <see cref="Rebuild"/> runs only after
    /// the repaired CNT has been spliced and fsynced, so every refusal it raises there lands on a
    /// package that is already mid-repair. The command's guard 2 calls this on the members it has
    /// already read, and those refusals then cost the user nothing at all.
    /// </para>
    /// <para>
    /// <see cref="Rebuild"/> still calls it. Pre-screening is a convenience for the CLI, never the
    /// enforcement: this class is not entitled to assume its caller asked first.
    /// </para>
    /// </summary>
    internal static void EnsureRebuildable(IReadOnlyDictionary<string, byte[]> members)
    {
        if (members.ContainsKey(PfsImageXmlPath))
            throw new InvalidDataException(
                $"the SI segment carries '{PfsImageXmlPath}', which this repair cannot reproduce; refusing to rebuild it");

        // TryGetValue, not the indexer: an indexer read of the FIRST path ran before the loop below
        // could reach it, so a package missing only that one member died on a bare
        // KeyNotFoundException and the loop's own message — which names the missing path — was
        // unreachable for it.
        byte[] napsMeta300 = members.TryGetValue(NapsMeta300Paths[0], out var first)
            ? first
            : throw new InvalidDataException($"the SI segment is missing '{NapsMeta300Paths[0]}'");
        foreach (string path in NapsMeta300Paths)
        {
            if (!members.TryGetValue(path, out var blob))
                throw new InvalidDataException($"the SI segment is missing '{path}'");
            // BuildMembers emits ONE blob at all four paths, so collapsing them is only correct
            // while they really are identical. Verified here rather than assumed.
            if (!blob.AsSpan().SequenceEqual(napsMeta300))
                throw new InvalidDataException(
                    $"'{path}' differs from '{NapsMeta300Paths[0]}'; the four naps_meta_3xx members are not " +
                    "interchangeable in this package, so the SI cannot be rebuilt from a single blob");
        }

        if (!members.ContainsKey(NapsMeta18Path))
            throw new InvalidDataException($"the SI segment is missing '{NapsMeta18Path}'");
    }

    /// <summary>
    /// Rebuilds the SI zip with the new playgo-chunk.dat and a CRC table recomputed over the
    /// repaired mount image (FIH + outer PFS + CNT).
    /// </summary>
    /// <param name="si">The original SI segment, the source of every member that is not replaced.</param>
    /// <param name="contentId">Content id; it names the <c>config/&lt;id&gt;/playgo-chunk.crc</c> member.</param>
    /// <param name="newChunkDat">The regenerated <c>playgo-chunk.dat</c> plaintext.</param>
    /// <param name="repairedMountImage">A stream over the repaired FIH + outer PFS + CNT.</param>
    /// <param name="mountImageLength">
    /// How much of <paramref name="repairedMountImage"/> the CRC table covers: <c>CntOffset +
    /// Cnt.Length</c>, i.e. the END OF THE CNT REGION, not the end of the CNT body. The final CRC
    /// block is reduced over a PARTIAL read of whatever remains, so an incorrect length changes the
    /// last entry's VALUE as well as the entry count.
    /// </param>
    /// <param name="progress">
    /// Receives the CRC pass's own log lines. That reduction walks the entire mount image, so on a
    /// 661 MB package it is the longest single step of the repair; without this it is silent.
    /// </param>
    internal static byte[] Rebuild(byte[] si, string contentId, byte[] newChunkDat,
                                   Stream repairedMountImage, long mountImageLength,
                                   Progress progress)
    {
        var members = ReadMembers(si);
        EnsureRebuildable(members);

        byte[] napsMeta300 = members[NapsMeta300Paths[0]];
        byte[] napsMeta18 = members[NapsMeta18Path];

        byte[] playGoChunkCrc =
            ProsperoPlayGo.BuildChunkCrc(repairedMountImage, mountImageLength,
                                         CancellationToken.None, line => progress.Detail(line));

        var rebuilt = ProsperoSiArchive.BuildMembers(
            contentId,
            null,               // pfsimage.xml: absent from this package, asserted above
            newChunkDat,
            napsMeta18,
            napsMeta300,
            playGoChunkCrc,
            null);              // finalizedMountImage: unused, the CRC is supplied verbatim

        return ProsperoSiArchive.WriteZip(rebuilt);
    }
}

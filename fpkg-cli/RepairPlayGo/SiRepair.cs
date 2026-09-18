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
    internal static byte[] Rebuild(byte[] si, string contentId, byte[] newChunkDat,
                                   Stream repairedMountImage, long mountImageLength)
    {
        var members = ReadMembers(si);

        if (members.ContainsKey(PfsImageXmlPath))
            throw new InvalidDataException(
                $"the SI segment carries '{PfsImageXmlPath}', which this repair cannot reproduce; refusing to rebuild it");

        byte[] napsMeta300 = members[NapsMeta300Paths[0]];
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

        byte[] napsMeta18 = members.TryGetValue(NapsMeta18Path, out var meta18)
            ? meta18
            : throw new InvalidDataException($"the SI segment is missing '{NapsMeta18Path}'");

        byte[] playGoChunkCrc =
            ProsperoPlayGo.BuildChunkCrc(repairedMountImage, mountImageLength);

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

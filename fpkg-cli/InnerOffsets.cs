using System.Reflection;
using LibProsperoPkg.PFS;
using LibProsperoPkg.PFS.Compression;
using LibProsperoPkg.PKG;
using LibProsperoPkg.Util;

namespace Fpkg.Cli;

/// <summary>
/// Maps an inner-PFS file's bytes back to the byte range they occupy in the .pkg FILE, which is
/// the coordinate space the PlayGo extent table uses.
///
/// <para>
/// There are three hops, and every one of them can fail to be a range:
/// </para>
/// <list type="number">
/// <item>inner inode -> inner logical image. Contiguous files carry one absolute offset; a
/// fragmented file carries a block list instead and has no single range.</item>
/// <item>inner logical image -> pfs_image.dat. The inner image is NAPS-packed. A STORED span is a
/// byte-for-byte copy, so the mapping is a constant shift; a COMPRESSED span has no byte-for-byte
/// image at all and is reported as unmapped rather than approximated.</item>
/// <item>pfs_image.dat -> the .pkg file. pfs_image.dat is itself a file in the outer PFS, so the
/// same contiguous/fragmented split applies, and the outer PFS sits at a fixed offset in the
/// package.</item>
/// </list>
///
/// <para>
/// Nothing here guesses. Each hop is computed from the library's own tables (the inode, the NAPS
/// plan, the package map); when a hop cannot produce a range the entry reports the reason and no
/// numbers, so a caller intersecting these ranges with a PlayGo extent never reads an invented
/// offset as a real one.
/// </para>
/// </summary>
internal sealed class InnerOffsets : IDisposable
{
    /// <param name="Start">Absolute byte offset from the start of the .pkg file — byte 0 is the
    /// first byte of the PKG header, so the 0x10000 FIH block is included in the origin.</param>
    /// <param name="Note">Why no range could be produced, or null when Start/End are real.</param>
    internal readonly record struct Range(long Start, long End, string? Note)
    {
        internal static Range Unmapped(string note) => new(-1, -1, note);
        internal bool Mapped => Note is null;
    }

    private readonly IDisposable outerSession;
    private readonly ProsperoNapsSpan[] spans;      // sorted by UncompressedOffset
    private readonly long outerPfsOffsetInPkg;
    private readonly long imageOffsetInOuter;       // pfs_image.dat data offset within the outer PFS
    private readonly int[]? imageBlocks;            // null when pfs_image.dat is contiguous
    private readonly int outerBlockSize;
    private readonly long imagePackedSize;

    internal long OuterPfsOffsetInPkg => outerPfsOffsetInPkg;
    internal long ImageOffsetInPkg => outerPfsOffsetInPkg + imageOffsetInOuter;
    internal bool ImageIsContiguous => imageBlocks is null;
    internal int SpanCount => spans.Length;
    internal int StoredSpanCount =>
        spans.Count(s => !IsDeduplicatedZeroSpan(s) && s.ShuffleIndex == 0 && s.KdePredictor == 0
                         && s.CompressedLength == s.UncompressedLength);
    internal int ZeroSpanCount => spans.Count(IsDeduplicatedZeroSpan);

    /// <summary>A human-readable dump of the three hops, for checking the mapping by hand.</summary>
    internal string DescribeLayout(long spansFrom = -1)
    {
        var lines = new List<string>
        {
            $"outer PFS at {outerPfsOffsetInPkg} in the .pkg, block size {outerBlockSize}",
            $"pfs_image.dat at {imageOffsetInOuter} in the outer PFS " +
            $"({ImageOffsetInPkg} in the .pkg), packed size {imagePackedSize}, " +
            (imageBlocks is null ? "contiguous" : $"{imageBlocks.Length} blocks, first {imageBlocks[0]}"),
            $"NAPS: {spans.Length} span(s), {StoredSpanCount} stored, {ZeroSpanCount} zero-dedup",
        };
        foreach (var s in spans.Take(4))
            lines.Add($"  span {s.Index}: ulog {s.UncompressedOffset}+{s.UncompressedLength} " +
                      $"-> stored {s.StoredOffset} clen {s.CompressedLength} " +
                      $"shuffle {s.ShuffleIndex} kde {s.KdePredictor} even {s.Even} odd {s.Odd}");
        // A window of the plan around a logical offset. The three-hop mapping is only ever wrong
        // in ways that show up here — a span whose StoredOffset does not advance with its
        // UncompressedOffset, or one far longer than the nominal 256 KiB — and nothing else in the
        // tool prints the plan, so a surprising Footprint cannot otherwise be told from a bug.
        if (spansFrom >= 0)
            foreach (var s in spans.Where(s => s.UncompressedOffset + s.UncompressedLength > spansFrom).Take(40))
                lines.Add($"  span {s.Index}: ulog {s.UncompressedOffset}+{s.UncompressedLength} " +
                          $"-> stored {s.StoredOffset} clen {s.CompressedLength} " +
                          $"shuffle {s.ShuffleIndex} kde {s.KdePredictor} even {s.Even} odd {s.Odd} " +
                          $"first {s.FirstChunkCompressedLength}");
        return string.Join('\n', lines);
    }

    internal InnerOffsets(string packagePath, string passcode, ProsperoNapsPlan plan,
                          CancellationToken cancellationToken)
    {
        spans = plan.Spans.OrderBy(s => s.UncompressedOffset).ToArray();

        var map = ProsperoPackageArchive.Inspect(packagePath);
        outerPfsOffsetInPkg = map.OuterPfsOffset;
        outerBlockSize = ProsperoPackageArchive.OuterBlockSize;

        outerSession = OpenOuterSession(packagePath, passcode, cancellationToken);
        var outer = (Stream)outerSession.GetType().GetProperty("Outer")!.GetValue(outerSession)!;
        var reader = new PfsReader(new LibProsperoPkg.Util.StreamReader(outer, 0L), 0uL, null, null, null,
                                   (long)map.OuterSuperblockIndex * outerBlockSize,
                                   encryptedDataAlreadyDecrypted: true);
        var image = reader.GetAllFiles().Single(f => f.name == "pfs_image.dat");
        // pfs_image.dat carries the inode `compressed` flag, and it is deliberately ignored here,
        // exactly as the library ignores it: OpenLogicalInnerPfs takes file.GetView() and hands
        // the RAW extent to the NAPS decoder rather than running it through PFSC. So the extent
        // is the NAPS packed image byte for byte, and its length is file.size (the physical
        // extent), which is the length OpenLogicalInnerPfs itself uses.
        imageOffsetInOuter = image.offset;
        imageBlocks = image.blocks;
        imagePackedSize = image.size;
    }

    /// <summary>
    /// The .pkg byte range a downloader must have in hand to reconstruct this file: the union of
    /// the packed footprints of every NAPS span covering the file's logical bytes.
    ///
    /// <para>
    /// Unlike <see cref="Locate"/> this is available whatever NAPS did, because a compressed span
    /// still lives somewhere in the package. It is a superset, not an identity: NAPS spans are
    /// 256 KiB of LOGICAL image, so the first and last span a file touches usually hold a
    /// neighbouring file's bytes too, and a de-duplicated zero span's footprint is the 8 or 16
    /// bytes that stand in for the zeros rather than the zeros themselves. Read it as "these
    /// package bytes are involved in this file", never as "these package bytes are this file".
    /// </para>
    /// </summary>
    internal Range Footprint(PfsReader.File file)
    {
        long length = file.size;
        if (length == 0) return Range.Unmapped("empty");
        if (file.blocks is not null) return Range.Unmapped("fragmented-in-inner-pfs");

        int i = FindSpanIndex(file.offset);
        if (i < 0) return Range.Unmapped("outside-naps-plan");
        long limit = file.offset + length, covered = file.offset;
        long lo = long.MaxValue, hi = long.MinValue;
        for (; i < spans.Length && covered < limit; i++)
        {
            var s = spans[i];
            if (s.UncompressedOffset > covered) return Range.Unmapped("gap-in-naps-plan");
            lo = Math.Min(lo, s.StoredOffset);
            hi = Math.Max(hi, s.StoredOffset + s.CompressedLength);
            covered = s.UncompressedOffset + s.UncompressedLength;
        }
        if (covered < limit) return Range.Unmapped("outside-naps-plan");

        long outerLo = OuterOffsetOf(lo);
        if (imageBlocks is not null && OuterOffsetOf(hi - 1) != outerLo + (hi - lo) - 1)
            return Range.Unmapped("fragmented-pfs-image");
        return new Range(outerPfsOffsetInPkg + outerLo,
                         outerPfsOffsetInPkg + outerLo + (hi - lo), null);
    }

    /// <summary>The .pkg byte range holding <paramref name="file"/>'s stored bytes.</summary>
    internal Range Locate(PfsReader.File file)
    {
        if (file.blocks is not null)
            return Range.Unmapped("fragmented-in-inner-pfs");
        long length = file.size;                 // the PHYSICAL extent, which is what is stored
        if (length == 0) return Range.Unmapped("empty");

        return LocateLogical(file.offset, length);
    }

    /// <summary>Maps a range of the inner LOGICAL image to the .pkg file.</summary>
    internal Range LocateLogical(long logicalOffset, long length)
    {
        // A file is typically far larger than one NAPS span (spans here are 256 KiB), so the walk
        // below crosses as many spans as it needs to. It yields a single range only when every
        // span the file touches is stored verbatim AND those spans sit back to back in the packed
        // image; the moment either fails, the file's bytes are not one contiguous run of package
        // bytes and no range is reported.
        int i = FindSpanIndex(logicalOffset);
        if (i < 0) return Range.Unmapped("outside-naps-plan");

        long packed = -1, packedEnd = -1, covered = logicalOffset;
        long limit = logicalOffset + length;
        for (; i < spans.Length && covered < limit; i++)
        {
            var s = spans[i];
            if (s.UncompressedOffset > covered) return Range.Unmapped("gap-in-naps-plan");
            // A span is a byte-for-byte copy only when NAPS stored it verbatim. Everything else —
            // a Kraken-compressed span, a shuffled one, a KDE-predicted one, or a de-duplicated
            // all-zero span (8 or 16 bytes in the package standing in for up to 128 KiB of zeros)
            // — has no package byte range corresponding to the file's bytes. The zero case is
            // named separately because it is the interesting answer, not a failure: those logical
            // bytes occupy essentially nothing in the package.
            if (IsDeduplicatedZeroSpan(s)) return Range.Unmapped("naps-zero-dedup");
            if (s.ShuffleIndex != 0) return Range.Unmapped("naps-shuffled");
            if (s.KdePredictor != 0) return Range.Unmapped("naps-kde-predicted");
            if (s.CompressedLength != s.UncompressedLength) return Range.Unmapped("naps-compressed");

            long from = s.StoredOffset + (covered - s.UncompressedOffset);
            long take = Math.Min(limit, s.UncompressedOffset + s.UncompressedLength) - covered;
            if (packed < 0) packed = from;
            else if (from != packedEnd) return Range.Unmapped("split-across-naps-spans");
            packedEnd = from + take;
            covered += take;
        }
        if (covered < limit) return Range.Unmapped("outside-naps-plan");
        if (packed < 0 || packed + length > imagePackedSize)
            return Range.Unmapped("outside-pfs-image");

        long outerStart = OuterOffsetOf(packed);
        if (imageBlocks is not null)
        {
            // Only a range if the whole extent stays inside one contiguous run of image blocks.
            long lastByte = packed + length - 1;
            if (OuterOffsetOf(lastByte) != outerStart + length - 1)
                return Range.Unmapped("fragmented-pfs-image");
        }
        return new Range(outerPfsOffsetInPkg + outerStart,
                         outerPfsOffsetInPkg + outerStart + length, null);
    }

    private long OuterOffsetOf(long packedOffset)
    {
        if (imageBlocks is null) return imageOffsetInOuter + packedOffset;
        long block = packedOffset / outerBlockSize;
        if (block >= imageBlocks.Length)
            throw new InvalidDataException("pfs_image.dat block index out of range.");
        return (long)imageBlocks[block] * outerBlockSize + packedOffset % outerBlockSize;
    }

    /// <summary>
    /// Transcribed by hand from ProsperoNapsImage.DecodeSpan, which treats a span matching this
    /// shape as a run of zeros and never reads its payload. The predicate is private there, so
    /// nothing checks the two stay in step automatically: if the library changes its dedup shape
    /// this will silently disagree. InnerOffsetsTests pins the observable consequence — the 31
    /// all-zero language chunks must report a package footprint of tens of bytes, not megabytes —
    /// which is what such a drift would break first.
    /// </summary>
    internal static bool IsDeduplicatedZeroSpan(ProsperoNapsSpan s) =>
        ((s.Odd == 1 && s.CompressedLength == 16 && s.UncompressedLength > 16)
         || (s.Odd == 0 && s.CompressedLength == 8 && s.UncompressedLength > 8
             && s.UncompressedLength <= 131072))
        && s.FirstChunkCompressedLength == 8 && s.Even == 1 && s.KdePredictor == 0
        && s.ShuffleIndex == 0;

    private int FindSpanIndex(long logicalOffset)
    {
        int lo = 0, hi = spans.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var s = spans[mid];
            if (logicalOffset < s.UncompressedOffset) hi = mid - 1;
            else if (logicalOffset >= s.UncompressedOffset + s.UncompressedLength) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    private static IDisposable OpenOuterSession(string packagePath, string passcode,
                                                CancellationToken cancellationToken)
    {
        var method = typeof(ProsperoPackageArchive).GetMethod(
            "OpenOuterPfsSession", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "LibProsperoPkg no longer has ProsperoPackageArchive.OpenOuterPfsSession; " +
                "re-derive inner-list's offset mapping against the shipped library.");
        try
        {
            return (IDisposable)method.Invoke(null, [packagePath, passcode, cancellationToken])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    public void Dispose() => outerSession.Dispose();
}

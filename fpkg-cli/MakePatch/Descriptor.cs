using LibProsperoPkg.PFS;
using LibProsperoPkg.PFS.Compression;
using LibProsperoPkg.PKG;

namespace Fpkg.Cli.MakePatch;

/// <summary>
/// What the builder records for a file after writing it (FileNode.StoreRaw, OnDiskOffset,
/// OnDiskSize, CompressionBlocks, PlaintextIntegrityBlocks), reconstructed from the base's
/// NAPS spans and meta18 integrity values. Injecting this makes the builder treat the base's
/// stored bytes as its own output for that file.
/// </summary>
internal sealed record Descriptor(
    bool StoreRaw, long OnDiskOffset, long OnDiskSize,
    IReadOnlyList<ProsperoInnerDataBlockChunk> Chunks,
    IReadOnlyList<ProsperoInnerPlaintextIntegrityBlock> Integrity)
{
    /// <summary>
    /// Inverse of ProsperoNapsLayoutBuilder.DeriveDataRegionBlocks. A contiguous, fully stored
    /// file becomes the WholeBlockRaw shape (no chunk list) the builder uses for executables and
    /// keystone; anything else becomes one chunk per 256 KiB block.
    /// </summary>
    internal static Descriptor From(BaseImage.FileInfo f, bool wholeBlockRaw)
    {
        var integrity = f.Blocks.Select(b => new ProsperoInnerPlaintextIntegrityBlock(
            b.UncompressedLength, b.Sha3, b.InputChecksum, b.RollingHash)).ToList();
        if (wholeBlockRaw)
        {
            if (!f.ContiguousRaw)
                throw new InvalidDataException($"{f.Path}: the builder stores it whole and raw, but the base does not");
            return new Descriptor(true, f.Blocks.Count == 0 ? 0 : f.Blocks[0].StoredOffset, f.Size, [], integrity);
        }

        var chunks = new List<ProsperoInnerDataBlockChunk>(f.Blocks.Count);
        foreach (var b in f.Blocks)
        {
            bool stored = b.CompressedLength == b.UncompressedLength;
            bool multi = !stored && b.Odd != 0;
            int flags = 0;
            if (!stored)
            {
                if ((b.Even & 4) != 0) flags |= 2;
                if ((b.Even & 2) != 0) flags |= 1;
                if (multi)
                {
                    if ((b.Odd & 4) != 0) flags |= 32;
                    if ((b.Odd & 2) != 0) flags |= 16;
                }
            }
            if (b.ShuffleIndex != 0)
                throw new InvalidDataException($"{f.Path}: shuffled block (PFSv3) is not supported");
            chunks.Add(new ProsperoInnerDataBlockChunk(
                b.CompressedLength, b.UncompressedLength, stored, multi,
                multi ? b.FirstChunkLength : b.CompressedLength, flags, b.StoredOffset));
        }
        return new Descriptor(chunks.All(c => c.IsStored),
            chunks.Count == 0 ? 0 : chunks[0].OnDiskOffset,
            chunks.Sum(c => (long)c.CompressedSize), chunks, integrity);
    }

    internal static Descriptor From(BaseImage.FileInfo f) => From(f, f.Executable && f.ContiguousRaw);

    /// <summary>
    /// Feeds this descriptor through the library's own NAPS derivation and compares the result
    /// with the base's spans. Returns null when every field agrees.
    /// </summary>
    internal string? CheckRoundTrip(BaseImage.FileInfo f)
    {
        var placement = new NapsFilePlacement
        {
            OnDiskOffset = OnDiskOffset, LogicalOffset = f.LogicalOffset, OnDiskSize = OnDiskSize,
            UncompressedSize = f.Size, StoreRaw = StoreRaw, CompressionBlocks = Chunks,
        };
        var entries = ProsperoNapsLayoutBuilder.DeriveDataRegionBlocks([placement], Array.Empty<long>());
        if (entries.Count != f.Blocks.Count) return $"{entries.Count} entries for {f.Blocks.Count} blocks";
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i]; var b = f.Blocks[i];
            if (e.OnDiskOffset != b.StoredOffset || e.LogicalOffset != f.LogicalOffset + (long)i * BaseImage.UBlock ||
                e.StreamLength != b.CompressedLength || e.EvenChunkCompressedLength != b.FirstChunkLength ||
                e.Even != b.Even || e.Odd != b.Odd)
                return $"block {i}: derived (off {e.OnDiskOffset}, len {e.StreamLength}, first {e.EvenChunkCompressedLength}, even {e.Even}, odd {e.Odd}) " +
                       $"vs base (off {b.StoredOffset}, len {b.CompressedLength}, first {b.FirstChunkLength}, even {b.Even}, odd {b.Odd})";
        }
        return null;
    }
}

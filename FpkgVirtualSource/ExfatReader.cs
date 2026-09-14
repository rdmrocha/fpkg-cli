using System.Buffers.Binary;
using LibProsperoPkg.Util;

namespace FpkgVirtualSource;

/// <summary>
/// Read-only exFAT parser over any <see cref="IMemoryReader"/>. Covers the subset game
/// images use: single FAT, standard directory entry sets, contiguous or FAT-chained
/// allocation. Ported from MkPFS's mkpfs/exfat.py.
/// </summary>
public sealed partial class ExfatReader
{
    private const uint FatEndOfChain = 0xFFFFFFFF;

    private readonly IMemoryReader _source;
    private readonly byte[] _fatEntry = new byte[4];

    public ExfatReader(IMemoryReader source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        Geometry = ExfatGeometry.Parse(source);
    }

    public ExfatGeometry Geometry { get; }

    /// <summary>
    /// The single lock protecting every read of <see cref="_source"/>, including the shared
    /// <see cref="_fatEntry"/> scratch buffer. <see cref="IMemoryReader"/> implementations are
    /// not reentrant, so one gate must serialise FAT walks, directory reads, and file-stream
    /// reads rather than each guarding its own piece independently.
    /// </summary>
    internal object Gate { get; } = new();

    internal long ClusterByteOffset(int cluster) =>
        (Geometry.ClusterHeapOffsetSectors + (long)(cluster - 2) * Geometry.SectorsPerCluster)
        * Geometry.BytesPerSector;

    private uint FatNext(int cluster)
    {
        long offset = Geometry.FatOffsetSectors * Geometry.BytesPerSector + (long)cluster * 4;
        lock (Gate)
        {
            _source.Read(offset, _fatEntry, 0, 4);
            return BinaryPrimitives.ReadUInt32LittleEndian(_fatEntry);
        }
    }

    /// <summary>
    /// Cluster numbers for one allocation. <paramref name="length"/> of zero or less means
    /// "follow the FAT to the end of the chain", which is how the root directory is walked.
    /// </summary>
    internal IEnumerable<int> IterateClusters(int firstCluster, bool noFatChain, long length)
    {
        if (firstCluster < 2) yield break;
        long clusterSize = Geometry.ClusterSize;

        if (noFatChain)
        {
            if (length <= 0) yield break;
            long count = (length + clusterSize - 1) / clusterSize;
            for (long i = 0; i < count; i++) yield return checked(firstCluster + (int)i);
            yield break;
        }

        long remaining = length > 0 ? (length + clusterSize - 1) / clusterSize : -1;
        int cluster = firstCluster;
        long seen = 0;
        while (cluster >= 2 && (uint)cluster < FatEndOfChain)
        {
            yield return cluster;
            seen++;
            if (remaining > 0 && seen >= remaining) yield break;
            if (seen > (long)Geometry.ClusterCount + 2)
                throw new ExfatException("cluster chain exceeds volume size (loop?)");
            cluster = unchecked((int)FatNext(cluster));
        }
    }
}

using System.Text;

namespace FpkgVirtualSource.Tests;

/// <summary>
/// Builds a minimal but structurally valid exFAT volume in memory: 512-byte sectors,
/// 64 KiB clusters, FAT at sector 128, cluster heap at sector 256, root at cluster 2.
/// Every file is allocated contiguously (NoFatChain), which is what MkPFS emits.
/// </summary>
internal sealed class TestVolume
{
    private const int SectorSize = 512;
    private const int ClusterSize = 65536;
    private const int FatSector = 128;
    private const int HeapSector = 256;
    private const int RootCluster = 2;
    private const int MaxClusters = 512;
    private const int EntriesPerCluster = ClusterSize / 32;

    private readonly byte[] _bytes = new byte[HeapSector * SectorSize + MaxClusters * ClusterSize];
    private readonly List<(string Path, byte[] Data)> _files = [];

    internal TestVolume Add(string path, byte[] data) { _files.Add((path, data)); return this; }

    /// <summary>Lays the volume out and returns it. Directories are inferred from paths.</summary>
    internal ByteSource Build()
    {
        BootSector.Build(fatOffsetSectors: FatSector, heapOffsetSectors: HeapSector,
                         clusterCount: MaxClusters, rootCluster: RootCluster).CopyTo(_bytes, 0);

        // Group by parent directory. One directory level is enough for the reader's contract.
        var byDir = _files.GroupBy(f => f.Path.Contains('/') ? f.Path[..f.Path.LastIndexOf('/')] : "")
                          .OrderBy(g => g.Key, StringComparer.Ordinal).ToList();

        int nextCluster = RootCluster + 1;   // cluster 2 is the root directory itself
        var dirEntries = new Dictionary<string, List<byte[]>> { [""] = [] };
        foreach (var group in byDir) dirEntries.TryAdd(group.Key, []);

        foreach (var group in byDir)
        {
            foreach (var (path, data) in group)
            {
                int first = data.Length == 0 ? 0 : nextCluster;
                if (data.Length > 0)
                {
                    data.CopyTo(_bytes, ClusterOffset(nextCluster));
                    nextCluster += (data.Length + ClusterSize - 1) / ClusterSize;
                }
                string name = path[(path.LastIndexOf('/') + 1)..];
                dirEntries[group.Key].AddRange(EntrySet(name, isDir: false, first, data.Length));
            }
        }

        // Subdirectories get a cluster each (contiguous / NoFatChain), then are referenced
        // from the root. A subdirectory's entries must fit the single cluster it is given --
        // this fixture does not (yet) express a multi-cluster, FAT-chained subdirectory.
        foreach (var group in byDir.Where(g => g.Key.Length > 0))
        {
            var subEntries = dirEntries[group.Key];
            if (subEntries.Count > EntriesPerCluster)
                throw new InvalidOperationException(
                    $"TestVolume fixture: directory '{group.Key}' has {subEntries.Count} " +
                    $"entries but its single-cluster allocation holds only {EntriesPerCluster}");

            int dirCluster = nextCluster++;
            int off = ClusterOffset(dirCluster);
            foreach (var e in subEntries) { e.CopyTo(_bytes, off); off += 32; }
            dirEntries[""].AddRange(EntrySet(group.Key, isDir: true, dirCluster, ClusterSize));
        }

        // The root directory is always walked via the FAT chain (never NoFatChain), so it is
        // laid out the same way a real volume's root would be: one or more clusters, each
        // chained through a real FAT entry and terminated with the 0xFFFFFFFF end-of-chain
        // marker. Relying on an unwritten (zero) FAT entry to fail the "cluster >= 2" guard
        // would let this fixture pass while exercising a different code path than a real
        // volume does.
        WriteFatChainedDirectory(RootCluster, dirEntries[""], ref nextCluster);

        return new ByteSource(_bytes);
    }

    /// <summary>
    /// Writes <paramref name="entries"/> across as many clusters as needed, starting at
    /// <paramref name="firstCluster"/> and allocating continuation clusters from
    /// <paramref name="nextCluster"/>, chaining them through real FAT entries terminated by
    /// the 0xFFFFFFFF end-of-chain marker.
    /// </summary>
    private void WriteFatChainedDirectory(int firstCluster, List<byte[]> entries, ref int nextCluster)
    {
        var clusters = new List<int> { firstCluster };
        int remaining = entries.Count - EntriesPerCluster;
        while (remaining > 0)
        {
            clusters.Add(nextCluster++);
            remaining -= EntriesPerCluster;
        }

        for (int i = 0; i < clusters.Count; i++)
        {
            int start = i * EntriesPerCluster;
            int count = Math.Min(EntriesPerCluster, entries.Count - start);
            int off = ClusterOffset(clusters[i]);
            for (int j = 0; j < count; j++) { entries[start + j].CopyTo(_bytes, off); off += 32; }

            uint next = i + 1 < clusters.Count ? (uint)clusters[i + 1] : 0xFFFFFFFF;
            WriteFatEntry(clusters[i], next);
        }
    }

    private void WriteFatEntry(int cluster, uint value) =>
        BitConverter.GetBytes(value).CopyTo(_bytes, FatSector * SectorSize + cluster * 4);

    private static int ClusterOffset(int cluster) =>
        HeapSector * SectorSize + (cluster - 2) * ClusterSize;

    /// <summary>File entry + stream extension + as many file-name entries as the name needs.</summary>
    private static List<byte[]> EntrySet(string name, bool isDir, int firstCluster, long length)
    {
        var units = Encoding.Unicode.GetBytes(name);
        int nameEntries = (name.Length + 14) / 15;

        var file = new byte[32];
        file[0] = 0x85;
        file[1] = (byte)(1 + nameEntries);
        BitConverter.GetBytes((ushort)(isDir ? 0x10 : 0x20)).CopyTo(file, 0x04);

        var stream = new byte[32];
        stream[0] = 0xC0;
        stream[1] = 0x02;                       // NoFatChain
        stream[3] = (byte)name.Length;
        BitConverter.GetBytes(length).CopyTo(stream, 0x08);   // ValidDataLength
        BitConverter.GetBytes(firstCluster).CopyTo(stream, 0x14);
        BitConverter.GetBytes(length).CopyTo(stream, 0x18);   // DataLength

        var set = new List<byte[]> { file, stream };
        for (int i = 0; i < nameEntries; i++)
        {
            var n = new byte[32];
            n[0] = 0xC1;
            int start = i * 30, len = Math.Min(30, units.Length - start);
            if (len > 0) Array.Copy(units, start, n, 2, len);
            set.Add(n);
        }
        return set;
    }
}

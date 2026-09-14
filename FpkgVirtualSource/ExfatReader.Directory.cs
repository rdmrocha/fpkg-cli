using System.Buffers.Binary;
using System.Text;

namespace FpkgVirtualSource;

public sealed partial class ExfatReader
{
    private const byte EntryEndOfDirectory = 0x00;
    private const byte EntryFile = 0x85;
    private const byte EntryStreamExtension = 0xC0;
    private const byte EntryFileName = 0xC1;
    private const byte AttrDirectory = 0x10;
    private const byte SecondaryFlagNoFatChain = 0x02;

    /// <summary>The directory tree rooted at the volume root.</summary>
    public IReadOnlyList<ExfatEntry> RootEntries() =>
        WalkDirectory(Geometry.RootDirCluster, noFatChain: false, length: 0, relativeDir: "");

    /// <summary>Every file (not directory) in the volume, ordered by path, case-insensitively.</summary>
    public IEnumerable<ExfatEntry> EnumerateFiles()
    {
        static IEnumerable<ExfatEntry> Walk(IReadOnlyList<ExfatEntry> nodes)
        {
            foreach (var n in nodes.OrderBy(n => n.RelativePath, StringComparer.OrdinalIgnoreCase))
                if (n.IsDirectory) foreach (var c in Walk(n.Children)) yield return c;
                else yield return n;
        }
        return Walk(RootEntries());
    }

    private List<byte[]> ReadDirectoryEntries(int firstCluster, bool noFatChain, long length)
    {
        var raw = new List<byte[]>();
        var cluster = new byte[Geometry.ClusterSize];
        foreach (int c in IterateClusters(firstCluster, noFatChain, length))
        {
            lock (Gate)
                _source.Read(ClusterByteOffset(c), cluster, 0, cluster.Length);
            for (int off = 0; off + 32 <= cluster.Length; off += 32)
            {
                if (cluster[off] == EntryEndOfDirectory) return raw;
                raw.Add(cluster.AsSpan(off, 32).ToArray());
            }
        }
        return raw;
    }

    private List<ExfatEntry> WalkDirectory(int firstCluster, bool noFatChain, long length, string relativeDir)
    {
        var entries = new List<ExfatEntry>();
        var raw = ReadDirectoryEntries(firstCluster, noFatChain, length);

        for (int i = 0; i < raw.Count; )
        {
            byte[] entry = raw[i];
            if (entry[0] != EntryFile) { i++; continue; }

            int secondaryCount = entry[1];
            ushort attrs = BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(0x04));
            var secondaries = raw.Skip(i + 1).Take(secondaryCount).ToList();
            i += 1 + secondaryCount;
            if (secondaries.Count < secondaryCount || secondaries.Count == 0) continue;

            byte[] stream = secondaries[0];
            if (stream[0] != EntryStreamExtension) continue;

            int nameLength = stream[3];
            long dataLength = BinaryPrimitives.ReadInt64LittleEndian(stream.AsSpan(0x18));
            int childCluster = BinaryPrimitives.ReadInt32LittleEndian(stream.AsSpan(0x14));
            bool childNoFat = (stream[1] & SecondaryFlagNoFatChain) != 0;

            var units = new List<byte>();
            foreach (byte[] s in secondaries.Skip(1))
                if (s[0] == EntryFileName) units.AddRange(s.AsSpan(2, 30).ToArray());
            string decoded = Encoding.Unicode.GetString(units.ToArray());
            string name = decoded.Length > nameLength ? decoded[..nameLength] : decoded;
            if (name.Length == 0) continue;

            bool isDir = (attrs & AttrDirectory) != 0;
            string relativePath = relativeDir.Length == 0 ? name : relativeDir + "/" + name;
            var node = new ExfatEntry
            {
                Name = name,
                RelativePath = relativePath,
                IsDirectory = isDir,
                FirstCluster = childCluster,
                Length = dataLength,
                NoFatChain = childNoFat,
            };
            if (isDir)
                node.Children.AddRange(WalkDirectory(childCluster, childNoFat, dataLength, relativePath));
            entries.Add(node);
        }
        return entries;
    }

    /// <summary>
    /// Opens a seekable, read-only view of <paramref name="entry"/>. Reads of the shared
    /// source are serialised on <see cref="Gate"/>, the single lock this reader owns for
    /// every access to the underlying (non-reentrant) source.
    /// </summary>
    public Stream OpenFile(ExfatEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsDirectory) throw new ExfatException("not a file: " + entry.RelativePath);

        int[] clusters = IterateClusters(entry.FirstCluster, entry.NoFatChain, entry.Length).ToArray();
        long available = clusters.Length * Geometry.ClusterSize;
        if (available < entry.Length)
            throw new ExfatException(
                $"file '{entry.RelativePath}' is truncated: {entry.Length - available} bytes short");

        return new ExfatFileStream(_source, Gate, clusters, Geometry.ClusterSize,
                                   entry.Length, ClusterByteOffset);
    }
}

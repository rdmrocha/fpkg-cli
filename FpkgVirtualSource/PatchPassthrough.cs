using System.Reflection;
using LibProsperoPkg.PFS;

namespace FpkgVirtualSource;

/// <summary>
/// make-patch's passthrough (site 25): the builder reuses a base package's stored file data
/// instead of compressing files again.
///
/// <para>Two calls are inserted at the head of BuildCore's physical placement run, before the
/// stock WholeBlockRaw round-up:</para>
/// <code>
/// cursor = PatchPassthrough.AdjustCursor(stream, cursor);
/// if (PatchPassthrough.Apply(file)) goto afterWrite;
/// </code>
/// <para><see cref="AdjustCursor"/> copies the base's pfs_image.dat data region, verbatim and at
/// the same offsets, into the new image the first time it runs, and from then on keeps the
/// cursor past it — so every file that IS written (the changed ones) lands after the base data.
/// <see cref="Apply"/> fills in, for an unchanged file, exactly the fields BuildCore would have
/// recorded after writing it — StoreRaw, OnDiskOffset, OnDiskSize, CompressionBlocks,
/// PlaintextIntegrityBlocks — pointing at the base's own bytes, and skips the write. Everything
/// downstream (layout, NAPS, meta18, outer PFS, CNT) consumes only those fields.</para>
///
/// <para>Inert unless the CLI sets <see cref="Enabled"/>: AdjustCursor returns its input and Apply
/// returns false, so every other build is untouched.</para>
/// </summary>
public static class PatchPassthrough
{
    /// <param name="RawOffset">Where the file's bytes start when the base stores them raw and
    /// contiguous (usable for the builder's WholeBlockRaw shape), else -1. Both shapes derive
    /// identical NAPS entries for such data, so the builder's own choice is followed.</param>
    public sealed record Entry(
        bool StoreRaw, long OnDiskOffset, long OnDiskSize, long Length, long RawOffset,
        IReadOnlyList<ProsperoInnerDataBlockChunk> Chunks,
        IReadOnlyList<ProsperoInnerPlaintextIntegrityBlock> Integrity);

    public static bool Enabled { get; private set; }
    private static string? basePackage;
    private static long baseImageOffset;
    private static long dataEnd;
    private static long[] live = [];
    private static bool prefilled;
    private static Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private static readonly HashSet<string> applied = new(StringComparer.Ordinal);

    /// <summary>Paths that were registered but never reached BuildCore's write loop.</summary>
    public static IEnumerable<string> Unapplied => entries.Keys.Where(k => !applied.Contains(k));
    public static int AppliedCount => applied.Count;

    /// <param name="imageOffset">Byte offset of pfs_image.dat in the base package (it must be
    /// stored contiguously).</param>
    /// <param name="files">Keyed by BuildCore's FullPath form, "/dir/name".</param>
    /// <param name="liveBlocks">Base pfs_image.dat blocks (64 KiB) below the data end that
    /// unchanged files still use, ascending. They are copied in this order, back to back, so
    /// blocks no surviving file touches are dropped and later data moves down by whole blocks;
    /// the entries' offsets must already be remapped to match.</param>
    public static void Configure(string package, long imageOffset, IReadOnlyList<long> liveBlocks,
                                 IDictionary<string, Entry> files)
    {
        basePackage = package;
        baseImageOffset = imageOffset;
        live = liveBlocks.ToArray();
        dataEnd = (long)live.Length * 0x10000;
        entries = new Dictionary<string, Entry>(files, StringComparer.Ordinal);
        applied.Clear();
        prefilled = false;
        Enabled = true;
    }

    public static void Reset()
    {
        Enabled = false;
        entries = new(StringComparer.Ordinal);
        applied.Clear();
        prefilled = false;
    }

    public static long AdjustCursor(Stream stream, long cursor)
    {
        if (!Enabled) return cursor;
        if (!prefilled)
        {
            if (cursor != 0)
                throw new InvalidOperationException(
                    $"make-patch: BuildCore wrote {cursor} bytes before the base data was placed");
            using var src = new FileStream(basePackage!, FileMode.Open, FileAccess.Read, FileShare.Read,
                                           1 << 20, FileOptions.SequentialScan);
            stream.Position = 0;
            var buf = new byte[8 << 20];
            // Runs of consecutive live blocks are copied in one go.
            for (int i = 0; i < live.Length;)
            {
                int j = i + 1;
                while (j < live.Length && live[j] == live[j - 1] + 1) j++;
                src.Position = baseImageOffset + live[i] * 0x10000;
                for (long left = (long)(j - i) * 0x10000; left > 0;)
                {
                    int n = (int)Math.Min(left, buf.Length);
                    src.ReadExactly(buf, 0, n);
                    stream.Write(buf, 0, n);
                    left -= n;
                }
                i = j;
            }
            prefilled = true;
        }
        long start = (dataEnd + 0xFFFF) & ~0xFFFFL;
        return Math.Max(cursor, start);
    }

    public static bool Apply(object node)
    {
        if (!Enabled) return false;
        var f = Fields.For(node.GetType());
        string path = (string)f.FullPath.GetValue(node)!;
        if (!entries.TryGetValue(path, out var e)) return false;

        long length = (long)f.Length.GetValue(node)!;
        bool wholeBlockRaw = (bool)f.WholeBlockRaw.GetValue(node)!;
        if (length != e.Length)
            throw new InvalidOperationException($"make-patch: {path} is {length} bytes in the tree, {e.Length} in the base");
        if (wholeBlockRaw)
        {
            if (e.RawOffset < 0)
                throw new InvalidOperationException(
                    $"make-patch: {path}: the builder stores it whole and raw but the base has it compressed");
            f.StoreRaw.SetValue(node, true);
            f.OnDiskOffset.SetValue(node, e.RawOffset);
            f.OnDiskSize.SetValue(node, e.Length);
            f.CompressionBlocks.SetValue(node, Array.Empty<ProsperoInnerDataBlockChunk>());
        }
        else
        {
            f.StoreRaw.SetValue(node, e.StoreRaw);
            f.OnDiskOffset.SetValue(node, e.OnDiskOffset);
            f.OnDiskSize.SetValue(node, e.OnDiskSize);
            f.CompressionBlocks.SetValue(node, e.Chunks);
        }
        f.PlaintextIntegrityBlocks.SetValue(node, e.Integrity);
        lock (applied) applied.Add(path);
        return true;
    }

    private sealed class Fields
    {
        private static Fields? cached;
        internal required FieldInfo FullPath, Length, WholeBlockRaw, StoreRaw, OnDiskOffset, OnDiskSize,
                                    CompressionBlocks, PlaintextIntegrityBlocks;

        internal static Fields For(Type t)
        {
            if (cached is not null) return cached;
            FieldInfo F(string name) =>
                t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"FileNode.{name} not found; re-derive make-patch's passthrough");
            return cached = new Fields
            {
                FullPath = F("FullPath"), Length = F("Length"), WholeBlockRaw = F("WholeBlockRaw"),
                StoreRaw = F("StoreRaw"), OnDiskOffset = F("OnDiskOffset"), OnDiskSize = F("OnDiskSize"),
                CompressionBlocks = F("CompressionBlocks"), PlaintextIntegrityBlocks = F("PlaintextIntegrityBlocks"),
            };
        }
    }
}

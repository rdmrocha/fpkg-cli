using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using LibProsperoPkg.PFS;
using LibProsperoPkg.PFS.Compression;
using LibProsperoPkg.PKG;

namespace Fpkg.Cli.MakePatch;

/// <summary>
/// Everything make-patch needs from the base package, read without decompressing any file:
/// the NAPS plan (one span per 256 KiB logical block), the inner file table (path, size,
/// logical offset), and the plaintext integrity values naps_meta_18.dat records for every
/// block. Together they let an unchanged file be described to the builder exactly as if it had
/// just been compressed again, pointing at the stored bytes already in the base.
/// </summary>
internal sealed class BaseImage
{
    internal const int UBlock = 0x40000;

    /// <summary>One 256 KiB logical block of a file, as the base stores it.</summary>
    internal readonly record struct Block(
        long StoredOffset, int CompressedLength, int UncompressedLength, int FirstChunkLength,
        byte Even, byte Odd, byte ShuffleIndex, uint TweakIndex,
        byte[] Sha3, ulong InputChecksum, ulong RollingHash);

    internal sealed record FileInfo(string Path, long Size, long LogicalOffset, bool Executable,
                                    IReadOnlyList<Block> Blocks)
    {
        /// <summary>True when every block is stored uncompressed and contiguous: the builder's
        /// WholeBlockRaw/StoreRaw shape (executables, keystone).</summary>
        public bool ContiguousRaw =>
            Blocks.Count > 0 &&
            Blocks.All(b => b.CompressedLength == b.UncompressedLength) &&
            Blocks.Select((b, i) => b.StoredOffset == Blocks[0].StoredOffset + (long)i * UBlock).All(x => x);
    }

    internal required string PackagePath { get; init; }
    internal required IReadOnlyList<FileInfo> Files { get; init; }
    internal required IReadOnlyList<ProsperoNapsSpan> Spans { get; init; }
    /// <summary>End of the stored file data in pfs_image.dat (exclusive).</summary>
    internal required long DataEnd { get; init; }
    internal required byte[] Meta18 { get; init; }

    internal static BaseImage Open(string packagePath, string passcode)
    {
        // Stored plaintext by some builders (this base), AES-XTS by others: the plaintext form
        // starts with the reversed "phdr" tag.
        byte[] meta18 = ReadSiMember(packagePath, "common/etc/naps_meta_18.dat");
        if (!meta18.AsSpan(0, 4).SequenceEqual("rdhp"u8)) meta18 = ProsperoNapsMeta.DecryptMeta18(meta18);
        var rec = ParseTlv(meta18);
        var fileRec = rec["file"];
        var paths = Encoding.UTF8.GetString(rec["fstr"]).Split('\0');
        byte[] i2ob = rec["i2ob"], ihsh = rec["ihsh"], rhsh = rec["rhsh"];

        using var session = InnerList.OpenSession(packagePath, passcode, CancellationToken.None);
        var plan = InnerList.PlanOf(session);
        var spans = plan.Spans.OrderBy(s => s.UncompressedOffset).ToArray();
        var inner = InnerList.FilesOf(session).ToDictionary(f => InnerList.Normalize(f.FullName), StringComparer.Ordinal);

        var files = new List<FileInfo>();
        int fileCount = fileRec.Length / 24;
        for (int i = 0; i < fileCount; i++)
        {
            var r = fileRec.AsSpan(i * 24, 24);
            long size = (long)BinaryPrimitives.ReadUInt64LittleEndian(r);
            int first = (int)BinaryPrimitives.ReadUInt32LittleEndian(r[8..]);
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(r[12..]);
            uint kind = BinaryPrimitives.ReadUInt32LittleEndian(r[16..]);
            bool exec = BinaryPrimitives.ReadUInt32LittleEndian(r[20..]) != 0;
            string path = i < paths.Length ? paths[i].TrimStart('/') : "";
            if (kind == 1001) continue;                       // *PFSmetadata
            if (path.StartsWith("uroot/", StringComparison.Ordinal)) path = path[6..];
            if (!inner.TryGetValue(path, out var node))
                throw new InvalidDataException($"meta18 names '{path}' but the inner PFS has no such file");
            if (node.size != size)
                throw new InvalidDataException($"{path}: meta18 size {size} != inode size {node.size}");

            int lo = LowerBound(spans, node.offset);
            var blocks = new List<Block>(count);
            for (int k = 0; k < count; k++)
            {
                var s = spans[lo + k];
                if (s.UncompressedOffset != node.offset + (long)k * UBlock)
                    throw new InvalidDataException($"{path}: block {k} span at logical {s.UncompressedOffset}, expected {node.offset + (long)k * UBlock}");
                int m = first + k;
                var ob = i2ob.AsSpan(m * 40, 40);
                long co = (long)BinaryPrimitives.ReadUInt64LittleEndian(ob);
                uint ps = BinaryPrimitives.ReadUInt32LittleEndian(ob[12..]);
                if (co != s.StoredOffset || ps != s.UncompressedLength)
                    throw new InvalidDataException($"{path}: block {k}: meta18 (Co {co}, Ps {ps}) != span (stored {s.StoredOffset}, ulen {s.UncompressedLength})");
                var h = ihsh.AsSpan(m * 48, 48);
                blocks.Add(new Block(s.StoredOffset, s.CompressedLength, s.UncompressedLength,
                    s.FirstChunkCompressedLength, s.Even, s.Odd, s.ShuffleIndex, s.TweakIndex,
                    h.Slice(8, 32).ToArray(),
                    BinaryPrimitives.ReadUInt64LittleEndian(h),
                    BinaryPrimitives.ReadUInt64LittleEndian(rhsh.AsSpan(m * 8, 8))));
            }
            long expectedBlocks = (size + UBlock - 1) / UBlock;
            if (blocks.Count != expectedBlocks)
                throw new InvalidDataException($"{path}: {blocks.Count} blocks for {size} bytes (expected {expectedBlocks})");
            files.Add(new FileInfo(path, size, node.offset, exec, blocks));
        }

        long dataEnd = files.SelectMany(f => f.Blocks)
                            .Select(b => b.StoredOffset + b.CompressedLength).DefaultIfEmpty(0).Max();
        return new BaseImage { PackagePath = packagePath, Files = files, Spans = spans, DataEnd = dataEnd, Meta18 = meta18 };
    }

    private static int LowerBound(ProsperoNapsSpan[] spans, long logical)
    {
        int lo = 0, hi = spans.Length;
        while (lo < hi) { int mid = (lo + hi) / 2; if (spans[mid].UncompressedOffset < logical) lo = mid + 1; else hi = mid; }
        return lo;
    }

    /// <summary>meta18 TLV: reversed 4-char tag, version, 3 zero bytes, LE64 length, payload.
    /// A tag that repeats (ibcl) keeps its first occurrence.</summary>
    internal static Dictionary<string, byte[]> ParseTlv(byte[] data)
    {
        var d = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        int pos = 0;
        while (pos + 16 <= data.Length)
        {
            string tag = new string([(char)data[pos + 3], (char)data[pos + 2], (char)data[pos + 1], (char)data[pos]]);
            long len = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos + 8));
            if (len < 0 || pos + 16 + len > data.Length)
                throw new InvalidDataException($"meta18 TLV broken at 0x{pos:x} (tag '{tag}', len {len}); head {Convert.ToHexString(data.AsSpan(0, Math.Min(64, data.Length)))}");
            d.TryAdd(tag, data.AsSpan(pos + 16, (int)len).ToArray());
            pos += 16 + (int)len;
        }
        return d;
    }

    /// <summary>Reads one member of the SI zip that ends the package.</summary>
    internal static byte[] ReadSiMember(string packagePath, string name)
    {
        using var fs = File.OpenRead(packagePath);
        long siStart = SiStart(fs);
        using var zip = new ZipArchive(new LihSubStream(fs, siStart, fs.Length - siStart), ZipArchiveMode.Read);
        var e = zip.GetEntry(name) ?? throw new InvalidDataException($"SI has no {name}");
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Finds the SI zip's first byte from its end-of-central-directory record.</summary>
    internal static long SiStart(Stream fs)
    {
        int tail = (int)Math.Min(fs.Length, 0x10000 + 22);
        var buf = new byte[tail];
        fs.Position = fs.Length - tail;
        fs.ReadExactly(buf);
        for (int i = tail - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i)) != 0x06054b50) continue;
            long eocd = fs.Length - tail + i;
            uint cdSize = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i + 12));
            uint cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i + 16));
            return eocd - cdSize - cdOffset;
        }
        throw new InvalidDataException("no SI zip at the end of the package");
    }

    /// <summary>A read-only window of a stream.</summary>
    internal sealed class LihSubStream(Stream inner, long start, long length) : Stream
    {
        private long pos;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => pos; set => pos = value; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (pos >= length) return 0;
            inner.Position = start + pos;
            int n = inner.Read(buffer, offset, (int)Math.Min(count, length - pos));
            pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) =>
            pos = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => pos + offset, _ => length + offset };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

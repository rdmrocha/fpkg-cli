namespace FpkgVirtualSource;

/// <summary>
/// make-patch without the extra disk space: the target's reused prefix — the base's live
/// pfs_image.dat blocks, laid end to end — is never written. It stays a hole in a sparse output
/// file; reads of it are served from the base package and writes to it are dropped.
///
/// <para>Two streams see that region. The inner-image read window (stage 3's source, through
/// <see cref="InPlaceImage.Open"/>) addresses it from 0, in pfs_image.dat coordinates; the outer
/// writer's output handle (site 27) addresses it from 0x10000, in package coordinates. The
/// dropped writes are the same bytes the base holds: make-patch's finaliser checks every reused
/// block's image digest against the base's, so a mismatch fails the patch instead of
/// shipping.</para>
///
/// <para>Inert unless the CLI calls <see cref="Configure"/>: both wrap calls return their input.</para>
/// </summary>
public static class PatchVirtual
{
    private const int Block = 0x10000;
    public static bool Enabled { get; private set; }
    private static string basePackage = "";
    private static long baseImageOffset;
    private static long[] live = [];

    public static void Configure(string package, long imageOffset, IReadOnlyList<long> liveBlocks)
    {
        basePackage = package;
        baseImageOffset = imageOffset;
        live = liveBlocks.ToArray();
        Enabled = true;
    }

    public static void Reset() => Enabled = false;

    /// <summary>Bytes in the virtual prefix (always whole blocks).</summary>
    public static long PrefixLength => Enabled ? (long)live.Length * Block : 0;

    /// <summary>Inner-image read window: prefix starts at 0.</summary>
    public static Stream WrapImage(Stream s) => Enabled ? new PrefixStream(s, 0) : s;

    /// <summary>Site 27: the outer writer's output handle; prefix starts at the outer PFS.</summary>
    public static Stream WrapOutput(Stream s) => Enabled ? new PrefixStream(s, Block) : s;

    private sealed class PrefixStream(Stream inner, long prefixStart) : Stream
    {
        private long pos;
        private FileStream? baseFile;
        private long PrefixEnd => prefixStart + PrefixLength;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => Math.Max(inner.Length, PrefixEnd);
        public override long Position { get => pos; set => pos = value; }
        public override long Seek(long offset, SeekOrigin origin) =>
            pos = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => pos + offset, _ => Length + offset };
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> dst)
        {
            if (dst.Length == 0) return 0;
            if (pos >= prefixStart && pos < PrefixEnd)
            {
                long rel = pos - prefixStart;
                int k = (int)(rel / Block), within = (int)(rel % Block);
                int n = Math.Min(dst.Length, Block - within);
                baseFile ??= new FileStream(basePackage, FileMode.Open, FileAccess.Read, FileShare.Read,
                                            1 << 20, FileOptions.RandomAccess);
                baseFile.Position = baseImageOffset + live[k] * Block + within;
                baseFile.ReadExactly(dst[..n]);
                pos += n;
                return n;
            }
            int limit = pos < prefixStart ? (int)Math.Min(dst.Length, prefixStart - pos) : dst.Length;
            inner.Position = pos;
            int got = inner.Read(dst[..limit]);
            pos += got;
            return got;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> src)
        {
            while (src.Length > 0)
            {
                if (pos >= prefixStart && pos < PrefixEnd)
                {
                    int n = (int)Math.Min(src.Length, PrefixEnd - pos);   // dropped: the base holds it
                    pos += n;
                    src = src[n..];
                    continue;
                }
                int m = pos < prefixStart ? (int)Math.Min(src.Length, prefixStart - pos) : src.Length;
                inner.Position = pos;
                inner.Write(src[..m]);
                pos += m;
                src = src[m..];
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { baseFile?.Dispose(); inner.Dispose(); }
            base.Dispose(disposing);
        }
    }
}

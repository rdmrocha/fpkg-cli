namespace FpkgVirtualSource;

/// <summary>
/// Replays LibProsperoPkg's deferred inner-layout permutation inside the final package, instead of
/// copying the image through it into a second file.
///
/// <para><b>Why this is not a copy loop.</b> The library picks its physical layout
/// (<c>SelectPhysicalLayout</c>: gap coalescing, then relocation alignment) only after the data is
/// written, so the move is a permutation of byte ranges within one image. Destination is usually
/// below source — but <c>Coalesce</c> appends leftovers to a running end that can sit above a
/// movable extent's source, so destination &gt; source is possible, and a forward copy would
/// overwrite bytes that are still somebody's source.</para>
///
/// <para><b>Two passes, and no dependency on the library's internals.</b> The extent table is an
/// internal type we cannot name from here, so pass 1 lets the library's own layout stream read
/// through a <see cref="RecordingSource"/> and simply writes down the ordered source ranges it
/// reads. Pass 2 replays the same stream, writing its output over the image — which is why
/// unmapped gaps need no special handling, the stream already serves them as zeros — while the
/// recorded order says exactly which bytes are still somebody's source and must be copied aside
/// before they are overwritten. A staged range is released the moment the last read that could
/// want it has gone by.</para>
///
/// <para><b>Failure is free.</b> Peak staging is bounded from the recorded order before a single
/// byte is written, conservatively (a gap makes the estimate larger, never smaller), so a
/// permutation needing more memory than the budget allows leaves the file exactly as it was and
/// the caller falls back to a spool.</para>
/// </summary>
internal static class LayoutRelocator
{
    /// <summary>How much of the image is moved per read/write.</summary>
    private const int ChunkSize = 4 * 1024 * 1024;

    /// <summary>
    /// One read the library's layout stream performed against the image, tagged with the
    /// destination chunk it was serving. <see cref="Chunk"/> is the frontier: a read is certainly
    /// consumed once the move has finished writing that chunk, and not before.
    /// </summary>
    internal readonly record struct SourceRead(long Start, long Length, int Chunk)
    {
        /// <summary>A bare byte range — staged ranges and intersections, which carry no frontier.</summary>
        internal SourceRead(long start, long length) : this(start, length, 0) { }

        internal long End => Start + Length;
    }

    /// <summary>
    /// The index of the first read that chunk <paramref name="chunk"/> has not certainly consumed
    /// yet. Reads are recorded in destination order, so <see cref="SourceRead.Chunk"/> is
    /// non-decreasing and a scan finds the boundary.
    ///
    /// <para>This one definition is what both the estimate and the move use, which is the point:
    /// they must agree, or the estimate is not a bound on what the move does. An earlier version
    /// counted read CALLS in the move and read BYTES in the estimate — two quantities that are
    /// unrelated once contiguous reads are merged or a gap is served without any read at all.</para>
    /// </summary>
    private static int PendingFrom(IReadOnlyList<SourceRead> reads, int chunk)
    {
        for (int i = 0; i < reads.Count; i++)
            if (reads[i].Chunk >= chunk) return i;
        return reads.Count;
    }

    internal static bool TryMaterialize(
        WindowStream target,
        Func<Stream> openSource,
        long length,
        long budgetBytes,
        Action<string>? log,
        out string? fallbackReason,
        int chunkSize = ChunkSize)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(openSource);
        fallbackReason = null;

        IReadOnlyList<SourceRead> reads = Discover(openSource, length, chunkSize);

        long peak = EstimatePeakStaging(reads, length, chunkSize);
        if (peak > budgetBytes)
        {
            fallbackReason =
                $"in-place layout relocation needs up to {peak:N0} bytes of staging, over the " +
                $"{budgetBytes:N0}-byte budget";
            return false;
        }

        Move(target, openSource, reads, length, chunkSize);
        target.SetLength(length);
        log?.Invoke($"Inner layout write: materialised in place ({length:N0} bytes, " +
                    $"{reads.Count:N0} mapped range(s), peak staging {peak:N0} bytes).");
        return true;
    }

    /// <summary>
    /// Pass 1. Reads the layout stream to its end and returns, in order, what it read from where.
    /// The bytes are discarded — only the addresses matter — and nothing is written.
    /// </summary>
    private static IReadOnlyList<SourceRead> Discover(Func<Stream> openSource, long length,
                                                     int chunkSize)
    {
        byte[] scratch = new byte[chunkSize];
        using Stream source = openSource();
        RecordingSource recorder = RecordingSource.Current
            ?? throw new InvalidOperationException(
                "The layout stream did not open through a recording window.");

        recorder.BeginRecording();
        for (long read = 0; read < length;)
        {
            int want = (int)Math.Min(scratch.Length, length - read);
            recorder.Chunk = (int)(read / chunkSize);
            source.ReadExactly(scratch.AsSpan(0, want));
            read += want;
        }
        var reads = Merge(recorder.Reads);
        recorder.End();
        return reads;
    }

    /// <summary>Joins reads that turned out to be contiguous, which most of them are.</summary>
    private static List<SourceRead> Merge(IReadOnlyList<SourceRead> raw)
    {
        var merged = new List<SourceRead>(raw.Count);
        foreach (SourceRead r in raw)
        {
            if (r.Length <= 0) continue;
            if (merged.Count != 0 && merged[^1].End == r.Start)
            {
                merged[^1] = merged[^1] with
                {
                    Length = merged[^1].Length + r.Length,
                    Chunk = Math.Max(merged[^1].Chunk, r.Chunk),
                };
                continue;
            }
            merged.Add(r);
        }
        return merged;
    }

    /// <summary>
    /// An upper bound on the displaced bytes held at once, computed without touching the file.
    ///
    /// <para>Consumption is estimated from cumulative read lengths, which ignores unmapped gaps and
    /// therefore under-estimates how far the reader has got. That makes the pending set larger than
    /// it really is, so the bound errs high: an over-estimate can only cost an unnecessary
    /// fallback, while an under-estimate could let the move run out of budget half-written.</para>
    /// </summary>
    private static long EstimatePeakStaging(IReadOnlyList<SourceRead> reads, long length,
                                            int chunkSize)
    {
        long peak = 0, live = 0;
        var staged = new List<SourceRead>();

        for (long w = 0; w < length; w += chunkSize)
        {
            int chunk = (int)(w / chunkSize);
            int n = (int)Math.Min(chunkSize, length - w);
            foreach (SourceRead need in Endangered(reads, PendingFrom(reads, chunk), w, n, staged))
            {
                staged.Add(need);
                live += need.Length;
                peak = Math.Max(peak, live);
            }
            live -= Release(staged, reads, PendingFrom(reads, chunk + 1));
        }
        return peak;
    }

    /// <summary>
    /// The sub-ranges of <c>[start, start+len)</c> that some read at or after
    /// <paramref name="from"/> still needs and that are not staged already.
    /// </summary>
    private static List<SourceRead> Endangered(
        IReadOnlyList<SourceRead> reads, int from, long start, long len, List<SourceRead> staged)
    {
        var need = new List<SourceRead>();
        for (int i = from; i < reads.Count; i++)
        {
            long lo = Math.Max(start, reads[i].Start);
            long hi = Math.Min(start + len, reads[i].End);
            if (lo >= hi) continue;
            need.Add(new SourceRead(lo, hi - lo));
        }

        foreach (SourceRead held in staged)
            Subtract(need, held);

        return Coalesce(need);
    }

    private static void Subtract(List<SourceRead> ranges, SourceRead held)
    {
        for (int i = ranges.Count - 1; i >= 0; i--)
        {
            SourceRead r = ranges[i];
            if (held.End <= r.Start || r.End <= held.Start) continue;
            ranges.RemoveAt(i);
            if (r.Start < held.Start) ranges.Add(new SourceRead(r.Start, held.Start - r.Start));
            if (held.End < r.End) ranges.Add(new SourceRead(held.End, r.End - held.End));
        }
    }

    private static List<SourceRead> Coalesce(List<SourceRead> ranges)
    {
        var result = new List<SourceRead>();
        foreach (SourceRead r in ranges.OrderBy(r => r.Start))
        {
            if (result.Count != 0 && r.Start <= result[^1].End)
            {
                result[^1] = new SourceRead(result[^1].Start,
                                            Math.Max(result[^1].End, r.End) - result[^1].Start);
                continue;
            }
            result.Add(r);
        }
        return result;
    }

    /// <summary>Drops staged ranges no remaining read wants, and reports how much that freed.</summary>
    private static long Release(List<SourceRead> staged, IReadOnlyList<SourceRead> reads, int from)
    {
        long freed = 0;
        for (int i = staged.Count - 1; i >= 0; i--)
        {
            bool needed = false;
            for (int j = from; j < reads.Count && !needed; j++)
                needed = staged[i].Start < reads[j].End && reads[j].Start < staged[i].End;
            if (!needed)
            {
                freed += staged[i].Length;
                staged.RemoveAt(i);
            }
        }
        return freed;
    }

    /// <summary>Pass 2. Replays the layout stream over the image itself.</summary>
    private static void Move(WindowStream target, Func<Stream> openSource,
                             IReadOnlyList<SourceRead> reads, long length, int chunkSize)
    {
        byte[] buffer = new byte[chunkSize];
        using Stream source = openSource();
        RecordingSource server = RecordingSource.Current
            ?? throw new InvalidOperationException(
                "The layout stream did not open through a recording window.");
        server.BeginServing();

        var staged = new List<SourceRead>();
        for (long w = 0; w < length; w += chunkSize)
        {
            int chunk = (int)(w / chunkSize);
            int n = (int)Math.Min(chunkSize, length - w);
            foreach (SourceRead need in Endangered(reads, PendingFrom(reads, chunk), w, n, staged))
            {
                server.Stage(need);
                staged.Add(need);
            }

            source.ReadExactly(buffer.AsSpan(0, n));
            target.Position = w;
            target.Write(buffer.AsSpan(0, n));

            Release(staged, reads, PendingFrom(reads, chunk + 1));
            server.ReleaseAllBut(staged);
        }
        server.End();
    }

    /// <summary>
    /// The window the library's layout stream reads through. In pass 1 it writes down every read;
    /// in pass 2 it serves reads that land on bytes the move has already overwritten out of the
    /// staged copies instead of out of the file.
    /// </summary>
    internal sealed class RecordingSource(WindowStream inner) : Stream
    {
        [ThreadStatic] private static RecordingSource? current;
        private readonly List<SourceRead> reads = [];
        private readonly Dictionary<long, byte[]> staged = [];
        private bool recording;

        internal static RecordingSource? Current => current;

        internal IReadOnlyList<SourceRead> Reads => reads;

        /// <summary>The destination chunk currently being served, stamped onto each read.</summary>
        internal int Chunk { get; set; }

        internal RecordingSource Install()
        {
            current = this;
            return this;
        }

        internal void BeginRecording()
        {
            recording = true;
            reads.Clear();
        }

        internal void BeginServing()
        {
            recording = false;
            staged.Clear();
        }

        internal void End()
        {
            recording = false;
            staged.Clear();
            if (ReferenceEquals(current, this)) current = null;
        }

        /// <summary>Copies a range aside before the move overwrites it.</summary>
        internal void Stage(SourceRead range)
        {
            byte[] copy = new byte[range.Length];
            inner.Position = range.Start;
            inner.ReadExactly(copy);
            staged[range.Start] = copy;
        }

        internal void ReleaseAllBut(List<SourceRead> keep)
        {
            foreach (long start in staged.Keys.ToList())
                if (!keep.Any(k => k.Start == start))
                    staged.Remove(start);
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0) return 0;

            int read = ServeOrRead(buffer);
            if (read > 0)
            {
                if (recording) reads.Add(new SourceRead(Position, read, Chunk));
                Position += read;
            }
            return read;
        }

        private int ServeOrRead(Span<byte> buffer)
        {
            foreach ((long start, byte[] bytes) in staged)
            {
                if (Position >= start && Position < start + bytes.Length)
                {
                    int take = (int)Math.Min(buffer.Length, start + bytes.Length - Position);
                    bytes.AsSpan((int)(Position - start), take).CopyTo(buffer);
                    return take;
                }
            }

            long clamp = buffer.Length;
            foreach ((long start, byte[] _) in staged)
                if (start > Position) clamp = Math.Min(clamp, start - Position);

            inner.Position = Position;
            return inner.Read(buffer[..(int)clamp]);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            return Position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        /// <summary>
        /// Disposal has to reach the window underneath: the FIH finalizer opens the finished
        /// package exclusively, so a handle left open here fails the build at the very last step,
        /// after every stage has already succeeded.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                staged.Clear();
                if (ReferenceEquals(current, this)) current = null;
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

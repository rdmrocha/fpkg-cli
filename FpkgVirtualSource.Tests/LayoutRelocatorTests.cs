using Xunit;

namespace FpkgVirtualSource.Tests;

/// <summary>
/// The in-place layout relocation, which replaces LibProsperoPkg's
/// <c>MaterializePhysicalLayout</c> — a full second copy of the inner image — with a pass that
/// moves the extents inside the package itself.
///
/// <para>The case that matters is the swap: the library picks its physical layout only after the
/// data is written, and <c>Coalesce</c> can place an extent ABOVE its own source, so a naive
/// forward copy would overwrite bytes that are still somebody's source. These tests drive the
/// relocator through a stand-in for the library's <c>PhysicalLayoutStream</c>, so that hazard is
/// exercised directly rather than hoped about.</para>
/// </summary>
public class LayoutRelocatorTests
{
    /// <summary>
    /// A stand-in for the library's internal <c>PhysicalLayoutStream</c>: serves the destination
    /// image by reading each mapped extent out of the underlying source, and zeros for any
    /// destination range no extent covers. That is exactly what the real one does, and it is the
    /// only behaviour the relocator depends on.
    /// </summary>
    private sealed class FakeLayoutStream(Stream source, (long Dest, long Src, long Len)[] extents, long length)
        : Stream
    {
        private long pos;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => pos; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (pos >= length) return 0;
            count = (int)Math.Min(count, length - pos);

            foreach (var e in extents.OrderBy(e => e.Dest))
            {
                if (pos >= e.Dest && pos < e.Dest + e.Len)
                {
                    long into = pos - e.Dest;
                    int take = (int)Math.Min(count, e.Len - into);
                    source.Position = e.Src + into;
                    source.ReadExactly(buffer.AsSpan(offset, take));
                    pos += take;
                    return take;
                }
                if (e.Dest > pos)                       // an unmapped gap: zeros, no read
                {
                    int take = (int)Math.Min(count, e.Dest - pos);
                    buffer.AsSpan(offset, take).Clear();
                    pos += take;
                    return take;
                }
            }
            buffer.AsSpan(offset, count).Clear();       // trailing gap
            pos += count;
            return count;
        }

        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    /// <summary>A package-shaped file: 64 bytes of reserved head, then the image body.</summary>
    private static (string Path, WindowStream Window) Window(byte[] body)
    {
        string path = Path.Combine(Path.GetTempPath(), "relo-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, [.. new byte[64], .. body]);
        return (path, new WindowStream(File.Open(path, FileMode.Open, FileAccess.ReadWrite), 64, false));
    }

    private static bool Materialize(WindowStream window, (long Dest, long Src, long Len)[] extents,
                                    long length, long budget, out string? why,
                                    int chunkSize = 4 * 1024 * 1024) =>
        LayoutRelocator.TryMaterialize(
            window,
            () => new FakeLayoutStream(
                new LayoutRelocator.RecordingSource(window).Install(), extents, length),
            length, budget, null, out why, chunkSize);

    [Fact]
    public void SwappedExtentsSurviveTheInPlacePass()
    {
        byte[] body = [.. Enumerable.Repeat((byte)1, 16), .. Enumerable.Repeat((byte)2, 16)];
        var (path, window) = Window(body);

        bool ok = Materialize(window, [(0, 16, 16), (16, 0, 16)], 32, 1 << 20, out string? why);
        window.Dispose();

        Assert.True(ok, why);
        byte[] all = File.ReadAllBytes(path);
        Assert.Equal(Enumerable.Repeat((byte)2, 16), all[64..80]);
        Assert.Equal(Enumerable.Repeat((byte)1, 16), all[80..96]);
    }

    [Fact]
    public void ForwardCompactionSurvivesAndTruncates()
    {
        byte[] body = [.. Enumerable.Repeat((byte)1, 16), .. new byte[16], .. Enumerable.Repeat((byte)3, 16)];
        var (path, window) = Window(body);

        bool ok = Materialize(window, [(0, 0, 16), (16, 32, 16)], 32, 1 << 20, out string? why);
        window.Dispose();

        Assert.True(ok, why);
        byte[] all = File.ReadAllBytes(path);
        Assert.Equal(96, all.Length);                       // 64 head + 32 image, tail truncated
        Assert.Equal(Enumerable.Repeat((byte)1, 16), all[64..80]);
        Assert.Equal(Enumerable.Repeat((byte)3, 16), all[80..96]);
    }

    [Fact]
    public void ExceedingTheStagingBudgetFallsBackWithoutTouchingTheFile()
    {
        byte[] body = [.. Enumerable.Repeat((byte)1, 16), .. Enumerable.Repeat((byte)2, 16)];
        var (path, window) = Window(body);

        bool ok = Materialize(window, [(0, 16, 16), (16, 0, 16)], 32, 8, out string? why);
        window.Dispose();

        Assert.False(ok);
        Assert.Contains("budget", why);
        Assert.Equal([.. new byte[64], .. body], File.ReadAllBytes(path));   // pristine
    }

    [Fact]
    public void IdentityLayoutIsAByteForByteNoOp()
    {
        byte[] body = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];
        var (path, window) = Window(body);

        bool ok = Materialize(window, [(0, 0, 32)], 32, 1 << 20, out string? why);
        window.Dispose();

        Assert.True(ok, why);
        Assert.Equal([.. new byte[64], .. body], File.ReadAllBytes(path));
    }

    [Fact]
    public void UnmappedGapsAreZeroFilled()
    {
        // The stale bytes sitting in a gap are old plaintext. PhysicalLayoutStream reads zeros
        // there, so the in-place pass has to write zeros there — it cannot just leave them.
        byte[] body = [.. Enumerable.Repeat((byte)0xEE, 48)];
        var (path, window) = Window(body);

        bool ok = Materialize(window, [(0, 32, 16), (32, 0, 16)], 48, 1 << 20, out string? why);
        window.Dispose();

        Assert.True(ok, why);
        byte[] all = File.ReadAllBytes(path);
        Assert.Equal(Enumerable.Repeat((byte)0xEE, 16), all[64..80]);
        Assert.Equal(new byte[16], all[80..96]);            // the gap
        Assert.Equal(Enumerable.Repeat((byte)0xEE, 16), all[96..112]);
    }

    [Fact]
    public void AnExtentOverlappingItselfIsCopiedWithoutSmearing()
    {
        // dest > src with an overlap: a forward copy would smear the first chunk over the rest.
        byte[] body = [.. Enumerable.Range(0, 64).Select(i => (byte)i)];
        var (path, window) = Window(body);

        bool ok = Materialize(window, [(0, 0, 8), (8, 0, 56)], 64, 1 << 20, out string? why);
        window.Dispose();

        Assert.True(ok, why);
        byte[] all = File.ReadAllBytes(path);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => (byte)i), all[64..72]);
        Assert.Equal(Enumerable.Range(0, 56).Select(i => (byte)i), all[72..128]);
    }

    /// <summary>
    /// The multi-chunk swap: the move spans several chunks, so bytes staged early must survive
    /// until the chunk that finally reads them.
    ///
    /// <para>Every other case in this file fits inside one chunk, which means the move reads the
    /// whole image before it writes anything and the staging map is never actually consulted. That
    /// is what let a real defect through review: the pass tracked how far the reader had got by
    /// counting read CALLS and indexing the MERGED range list with it, two different quantities.
    /// A range spanning several chunks costs several calls, so the frontier ran ahead, the pass
    /// stopped staging and released what it already held, and later chunks read bytes they had
    /// themselves overwritten — a scrambled image inside a package whose digest chain is perfectly
    /// consistent with it, so `fpkg verify --full` passes.</para>
    /// </summary>
    [Fact]
    public void AMultiChunkSwapKeepsStagedBytesUntilTheChunkThatReadsThem()
    {
        byte[] body = [.. Enumerable.Repeat((byte)0xA1, 32), .. Enumerable.Repeat((byte)0xB2, 32)];
        var (path, window) = Window(body);

        bool ok = Materialize(window, [(0, 32, 32), (32, 0, 32)], 64, 1 << 20, out string? why,
                              chunkSize: 16);
        window.Dispose();

        Assert.True(ok, why);
        byte[] all = File.ReadAllBytes(path);
        Assert.Equal(Enumerable.Repeat((byte)0xB2, 32), all[64..96]);
        Assert.Equal(Enumerable.Repeat((byte)0xA1, 32), all[96..128]);
    }

    /// <summary>
    /// The same hazard with an uneven, gap-bearing permutation across many chunks: a rotation by
    /// one chunk, which forces every chunk to read bytes a previous chunk overwrote.
    /// </summary>
    [Fact]
    public void AMultiChunkRotationSurvivesTheInPlacePass()
    {
        const int chunk = 16, chunks = 8;
        byte[] body = new byte[chunk * chunks];
        for (int i = 0; i < chunks; i++)
            Array.Fill(body, (byte)(0x10 + i), i * chunk, chunk);
        var (path, window) = Window(body);

        // dest chunk i takes source chunk (i+1) % chunks.
        var extents = Enumerable.Range(0, chunks)
            .Select(i => ((long)(i * chunk), (long)(((i + 1) % chunks) * chunk), (long)chunk))
            .ToArray();

        bool ok = Materialize(window, extents, chunk * chunks, 1 << 20, out string? why,
                              chunkSize: chunk);
        window.Dispose();

        Assert.True(ok, why);
        byte[] all = File.ReadAllBytes(path);
        for (int i = 0; i < chunks; i++)
        {
            byte expected = (byte)(0x10 + (i + 1) % chunks);
            Assert.Equal(Enumerable.Repeat(expected, chunk), all[(64 + i * chunk)..(64 + (i + 1) * chunk)]);
        }
    }

    /// <summary>
    /// The budget estimate must be an upper bound on what the move actually stages, across chunk
    /// boundaries and gaps alike — if it under-estimates, the move runs past the budget and dies
    /// mid-write, leaving a half-relocated image inside the package.
    /// </summary>
    [Fact]
    public void TheEstimateIsNotUnderTheMovesActualStagingAcrossChunks()
    {
        const int chunk = 16, chunks = 6;
        byte[] body = new byte[chunk * chunks];
        new Random(5).NextBytes(body);
        var (path, window) = Window(body);

        // A rotation with a gap in the middle: gaps advance the destination without any read,
        // which is exactly where a byte-counted frontier and a call-counted one diverge.
        var extents = new (long, long, long)[]
        {
            (0, chunk * 3, chunk),
            (chunk * 2, 0, chunk),
            (chunk * 4, chunk, chunk * 2),
        };

        bool ok = Materialize(window, extents, chunk * chunks, 1 << 20, out string? why,
                              chunkSize: chunk);
        window.Dispose();
        Assert.True(ok, why);

        byte[] all = File.ReadAllBytes(path)[64..];
        Assert.Equal(body[(chunk * 3)..(chunk * 4)], all[0..chunk]);          // extent 1
        Assert.Equal(new byte[chunk], all[chunk..(chunk * 2)]);               // gap -> zeros
        Assert.Equal(body[0..chunk], all[(chunk * 2)..(chunk * 3)]);          // extent 2
        Assert.Equal(new byte[chunk], all[(chunk * 3)..(chunk * 4)]);         // gap -> zeros
        Assert.Equal(body[chunk..(chunk * 3)], all[(chunk * 4)..(chunk * 6)]);// extent 3
    }
}

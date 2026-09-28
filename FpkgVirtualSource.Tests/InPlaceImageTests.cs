using Xunit;

namespace FpkgVirtualSource.Tests;

/// <summary>
/// The façade every in-place patch site calls. These tests pin the two properties the whole
/// feature rests on: it is completely inert until the CLI switches it on, and a window path never
/// reaches an operation that would damage the package it points into.
/// </summary>
[Collection("InPlaceImage")]
public class InPlaceImageTests : IDisposable
{
    public InPlaceImageTests() => InPlaceImage.Reset();

    public void Dispose() => InPlaceImage.Reset();

    [Fact]
    public void DisabledIsCompletelyInert()
    {
        InPlaceImage.Enabled = false;
        Assert.Equal("/tmp/spool.dat",
            InPlaceImage.SpoolPathFor("/tmp/spool.dat", "/out/x.pkg", prepositioned: true));
        Assert.True(InPlaceImage.DeferLayout());
        Assert.False(InPlaceImage.IsWindow("/tmp/spool.dat"));
    }

    [Fact]
    public void SpoolPathBecomesAWindowOnlyWhenPrepositioned()
    {
        InPlaceImage.Enabled = true;
        Assert.Equal("/tmp/spool.dat",
            InPlaceImage.SpoolPathFor("/tmp/spool.dat", "/out/x.pkg", prepositioned: false));

        string window = InPlaceImage.SpoolPathFor("/tmp/spool.dat", "/out/x.pkg", prepositioned: true);
        Assert.Equal("fpkgwin:/out/x.pkg@65536", window);
        Assert.True(InPlaceImage.IsWindow(window));
        Assert.False(InPlaceImage.DeferLayout());
    }

    [Fact]
    public void GuardFullPathLeavesWindowsAloneAndResolvesEverythingElse()
    {
        Assert.Equal("fpkgwin:/out/x.pkg@65536", InPlaceImage.GuardFullPath("fpkgwin:/out/x.pkg@65536"));
        Assert.Equal(Path.GetFullPath("rel.dat"), InPlaceImage.GuardFullPath("rel.dat"));
    }

    [Fact]
    public void OpenCreateReservesTheHeadAndReportsTheWindowLength()
    {
        InPlaceImage.Enabled = true;
        string pkg = Path.Combine(Path.GetTempPath(), "ip-" + Guid.NewGuid().ToString("N") + ".pkg");
        string window = InPlaceImage.SpoolPathFor("/tmp/spool.dat", pkg, prepositioned: true);

        using (Stream s = InPlaceImage.Open(window, FileMode.Create, FileAccess.ReadWrite,
                                            FileShare.None, 1 << 20, FileOptions.SequentialScan))
        {
            s.Write(new byte[131072], 0, 131072);
            s.SetLength(131072);
        }

        Assert.Equal(65536 + 131072, new FileInfo(pkg).Length);
        Assert.Equal(131072, InPlaceImage.LengthOf(window));
        File.Delete(pkg);
    }

    [Fact]
    public void OpenDoesNotTruncateAPackageThatAlreadyHoldsTheImage()
    {
        // Stage 3 opens the output for writing AFTER stage 1 has put the plaintext image in it.
        // A Create-mode open that truncated would destroy exactly the bytes it is about to read.
        InPlaceImage.Enabled = true;
        string pkg = Path.Combine(Path.GetTempPath(), "ip-" + Guid.NewGuid().ToString("N") + ".pkg");
        string window = InPlaceImage.SpoolPathFor("/tmp/spool.dat", pkg, prepositioned: true);

        using (Stream s = InPlaceImage.Open(window, FileMode.Create, FileAccess.ReadWrite,
                                            FileShare.None, 4096, FileOptions.None))
            s.Write([1, 2, 3, 4], 0, 4);

        using (Stream s = InPlaceImage.Open(window, FileMode.Open, FileAccess.Read,
                                            FileShare.Read, 4096, FileOptions.None))
        {
            var buf = new byte[4];
            s.ReadExactly(buf);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, buf);
        }
        File.Delete(pkg);
    }

    [Fact]
    public void DisposingAWindowReleasesTheUnderlyingFileHandle()
    {
        // The FIH finalizer opens the finished package exclusively. Every window the shim hands
        // out is wrapped, and a wrapper that does not pass disposal on leaves a live handle that
        // fails the build at the last step, after every stage has already succeeded.
        InPlaceImage.Enabled = true;
        string pkg = Path.Combine(Path.GetTempPath(), "ip-" + Guid.NewGuid().ToString("N") + ".pkg");
        string window = InPlaceImage.SpoolPathFor("/tmp/spool.dat", pkg, prepositioned: true);

        using (Stream s = InPlaceImage.Open(window, FileMode.Create, FileAccess.ReadWrite,
                                            FileShare.None, 4096, FileOptions.None))
            s.Write([1, 2, 3, 4], 0, 4);
        using (Stream s = InPlaceImage.Open(window, FileMode.Open, FileAccess.Read,
                                            FileShare.Read, 4096, FileOptions.None))
            s.ReadByte();

        using (File.Open(pkg, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        File.Delete(pkg);
    }

    [Fact]
    public void CreateOnAWindowClearsTheReservedHeadToo()
    {
        // Stock stage 3 opens the output FileMode.Create, so everything below 0x10000 starts as a
        // hole and only the finaliser's own bytes end up there. In place nothing else truncates
        // the file, so Create on a window has to clear the whole file, not just the window —
        // otherwise a rebuild over an existing package leaves the previous package's head bytes
        // wherever the finaliser does not write, which is a byte-parity divergence against --spool.
        InPlaceImage.Enabled = true;
        string pkg = Path.Combine(Path.GetTempPath(), "ip-" + Guid.NewGuid().ToString("N") + ".pkg");
        File.WriteAllBytes(pkg, [.. Enumerable.Repeat((byte)0xFF, 200000)]);
        string window = InPlaceImage.SpoolPathFor("/tmp/spool.dat", pkg, prepositioned: true);

        using (Stream s = InPlaceImage.Open(window, FileMode.Create, FileAccess.ReadWrite,
                                            FileShare.None, 4096, FileOptions.None))
            s.Write([1, 2, 3, 4], 0, 4);

        byte[] all = File.ReadAllBytes(pkg);
        Assert.Equal(InPlaceImage.OuterPfsOffset + 4, all.Length);
        Assert.Equal(new byte[InPlaceImage.OuterPfsOffset], all[..(int)InPlaceImage.OuterPfsOffset]);
        File.Delete(pkg);
    }

    [Fact]
    public void OpenFallsThroughToARealFileForOrdinaryPaths()
    {
        string path = Path.Combine(Path.GetTempPath(), "plain-" + Guid.NewGuid().ToString("N"));
        using (Stream s = InPlaceImage.Open(path, FileMode.Create, FileAccess.ReadWrite,
                                            FileShare.None, 4096, FileOptions.None))
            Assert.IsType<FileStream>(s);
        File.Delete(path);
    }

    [Fact]
    public void TryDeleteWindowNeverDeletesTheOutputPackage()
    {
        InPlaceImage.Enabled = true;
        string pkg = Path.Combine(Path.GetTempPath(), "keep-" + Guid.NewGuid().ToString("N") + ".pkg");
        File.WriteAllBytes(pkg, new byte[16]);
        string window = InPlaceImage.SpoolPathFor("/tmp/spool.dat", pkg, prepositioned: true);

        Assert.True(InPlaceImage.TryDeleteWindow(window));          // handled, and a no-op
        Assert.True(File.Exists(pkg));
        Assert.False(InPlaceImage.TryDeleteWindow("/tmp/spool.dat"));   // not ours; caller deletes
        File.Delete(pkg);
    }

    [Fact]
    public void TheOutputOpenIsRelaxedOnlyWhileTheSingleFileBuildIsOn()
    {
        // With the single-file build off, the outer writer must open the output exactly as stock
        // LibProsperoPkg does: Create is what clears a stale package from an earlier run, and
        // relaxing it unconditionally would let stale bytes survive under the new length.
        InPlaceImage.Enabled = false;
        Assert.Equal((int)FileMode.Create, InPlaceImage.OutputFileMode());
        Assert.Equal((int)FileShare.None, InPlaceImage.OutputFileShare());

        // In place the output already holds the plaintext image, and the read window needs a
        // second handle on it.
        InPlaceImage.Enabled = true;
        Assert.Equal((int)FileMode.OpenOrCreate, InPlaceImage.OutputFileMode());
        Assert.Equal((int)FileShare.ReadWrite, InPlaceImage.OutputFileShare());
    }

    [Fact]
    public void ABudgetOverflowSpoolsTheImageAndKeepsServingIt()
    {
        // The library's own MaterializePhysicalLayout ends in Move(tmp, path). `path` is the
        // window string, so letting that body run would leave a junk file named "fpkgwin:..." and
        // a package with no image. The fallback therefore happens in here, and reports handled.
        InPlaceImage.Enabled = true;
        InPlaceImage.MaxRelocationBytes = 8;
        string temp = Path.Combine(Path.GetTempPath(), "ip-fb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        InPlaceImage.FallbackDirectory = temp;

        string pkg = Path.Combine(temp, "out.pkg");
        string window = InPlaceImage.SpoolPathFor("/tmp/spool.dat", pkg, prepositioned: true);
        byte[] body = [.. Enumerable.Repeat((byte)1, 16), .. Enumerable.Repeat((byte)2, 16)];
        using (Stream s = InPlaceImage.Open(window, FileMode.Create, FileAccess.ReadWrite,
                                            FileShare.None, 4096, FileOptions.None))
            s.Write(body, 0, body.Length);

        // A layout that swaps the two halves, which needs more staging than the budget allows.
        bool handled = InPlaceImage.MaterializeInPlace(window, () => Swapped(window), 32);

        Assert.True(handled);
        Assert.NotNull(InPlaceImage.FallbackReason);
        Assert.Equal(32, InPlaceImage.LengthOf(window));

        using (Stream s = InPlaceImage.Open(window, FileMode.Open, FileAccess.Read,
                                            FileShare.Read, 4096, FileOptions.None))
        {
            var got = new byte[32];
            s.ReadExactly(got);
            Assert.Equal(Enumerable.Repeat((byte)2, 16), got[..16]);
            Assert.Equal(Enumerable.Repeat((byte)1, 16), got[16..]);
        }
        Directory.Delete(temp, true);
    }

    /// <summary>
    /// A stand-in for the library's layout stream that serves the window's two 16-byte halves the
    /// other way round. It must read LAZILY, through the window the shim just opened, or the
    /// relocator would never see the reads it needs to record.
    /// </summary>
    private static Stream Swapped(string window) => new SwapStream(
        InPlaceImage.Open(window, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.None));

    private sealed class SwapStream(Stream source) : Stream
    {
        private long pos;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 32;
        public override long Position { get => pos; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (pos >= 32) return 0;
            int take = (int)Math.Min(count, (pos < 16 ? 16 : 32) - pos);
            source.Position = pos < 16 ? pos + 16 : pos - 16;
            source.ReadExactly(buffer.AsSpan(offset, take));
            pos += take;
            return take;
        }

        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) source.Dispose();
            base.Dispose(disposing);
        }
    }

    [Fact]
    public void TheCliNeverSetsANapsIntegrityProvider()
    {
        // ProsperoNapsMeta re-reads the physical inner image for obdg/obcc when an integrity
        // provider is configured, and BuildFromFsTreeToSpool then materialises a third copy.
        // Both are incompatible with building in place, and both are reachable only through a
        // build option the CLI must never set.
        string program = File.ReadAllText(Path.Combine(RepoRoot(), "fpkg-cli", "Program.cs"));
        Assert.DoesNotContain("NapsIntegrityProvider", program);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VERSION")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}

using Fpkg.Cli.RepairPlayGo;
using Xunit;

public class PackageRegionsTests
{
    [SkippableFact]
    public void SplitsTheKnownRegions()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var r = PackageRegions.Load(TestPackage.Path);
        Assert.Equal(0x10000, r.OuterPfsOffset);
        Assert.Equal(596_705_280, r.OuterPfsSize);
        Assert.Equal(596_770_816, r.CntOffset);
        Assert.Equal(63_569_920, r.Cnt.Length);
        Assert.Equal(665_774, r.Si.Length);

        // No padding past the body — Task 10's CRC length rule depends on this.
        Assert.Equal((long)r.Cnt.Length,
            (long)CntHeader.U64(r.Cnt, CntHeader.BodyOffset)
          + (long)CntHeader.U64(r.Cnt, CntHeader.BodySize));
    }

    /// <summary>
    /// The regression that matters most on a large package: Load must touch the CNT and the SI and
    /// nothing else. It used to call ProsperoPackageArchive.Split with Stream.Null as the outer-PFS
    /// destination, which discarded the writes but still READ the whole payload — ~600 MB here, 90 GB
    /// on a user's package, on every run including --dry-run.
    ///
    /// <para>
    /// The bound is the two regions plus a megabyte of slack, so that buffering or Inspect's own
    /// header probe cannot make it flaky. That slack is nearly three orders of magnitude smaller
    /// than the payload, so no read of the payload — whole or partial — can slip under it.
    /// </para>
    /// </summary>
    [SkippableFact]
    public void LoadReadsOnlyTheCntAndSiNotThePayload()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");

        CountingStream? counter = null;
        var r = PackageRegions.Load(TestPackage.Path, progress: null,
                                    openSourceStream: p => counter = new CountingStream(File.OpenRead(p)));

        Assert.NotNull(counter);
        long regions = r.Cnt.LongLength + r.Si.LongLength;
        Assert.True(counter!.BytesRead >= regions,
            $"Load read {counter.BytesRead:N0} bytes, fewer than the {regions:N0} it returned");
        Assert.True(counter.BytesRead <= regions + (1L << 20),
            $"Load read {counter.BytesRead:N0} bytes for {regions:N0} bytes of CNT+SI — " +
            $"the {r.OuterPfsSize:N0}-byte payload is being read again");
    }

    /// <summary>Counts every byte handed back by the inner stream. Reads only; nothing else is used.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        internal long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            int n = inner.Read(buffer);
            BytesRead += n;
            return n;
        }

        public override int ReadByte()
        {
            int b = inner.ReadByte();
            if (b >= 0) BytesRead++;
            return b;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => inner.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    [SkippableFact]
    public void SplicingBackTheOriginalRegionsReproducesTheFile()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var r = PackageRegions.Load(TestPackage.Path);
        var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        try
        {
            r.WriteTo(tmp, r.Cnt, r.Si);
            Assert.Equal(Bytes.Sha256(TestPackage.Path), Bytes.Sha256(tmp));
        }
        finally { File.Delete(tmp); }
    }

    /// <summary>
    /// A CNT-only file is what Split's own cnt output is: ProsperoPkgReader.Read accepts it
    /// (DetectType reports Meta) but leaves Fih null, so Load must refuse it with a clear
    /// error rather than a NullReferenceException.
    /// </summary>
    [SkippableFact]
    public void RefusesAPackageWithNoFihHeader()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var cnt = PackageRegions.Load(TestPackage.Path).Cnt;
        var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        try
        {
            File.WriteAllBytes(tmp, cnt);
            var ex = Assert.Throws<InvalidDataException>(() => PackageRegions.Load(tmp));
            Assert.Contains("FIH", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(tmp); }
    }
}

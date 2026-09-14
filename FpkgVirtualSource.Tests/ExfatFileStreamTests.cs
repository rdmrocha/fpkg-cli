using System.Security.Cryptography;
using Xunit;

namespace FpkgVirtualSource.Tests;

public class ExfatFileStreamTests
{
    private static readonly byte[] Payload = Make(200_000);

    private static byte[] Make(int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)(i * 31 + (i >> 11));
        return b;
    }

    private static Stream Open()
    {
        var r = new ExfatReader(new TestVolume().Add("data/big.bin", Payload).Build());
        return r.OpenFile(r.EnumerateFiles().Single());
    }

    [Fact]
    public void ReportsLengthAndCapabilities()
    {
        using var s = Open();
        Assert.Equal(Payload.Length, s.Length);
        Assert.True(s.CanRead);
        Assert.True(s.CanSeek);
        Assert.False(s.CanWrite);
    }

    [Fact]
    public void SequentialReadReturnsExactBytes()
    {
        using var s = Open();
        var got = new MemoryStream();
        s.CopyTo(got, 4096);
        Assert.Equal(Payload, got.ToArray());
    }

    [Fact]
    public void ReadSpansClusterBoundaries()
    {
        using var s = Open();
        s.Position = 65536 - 10;               // straddle the first cluster boundary
        var buf = new byte[20];
        s.ReadExactly(buf);
        Assert.Equal(Payload.AsSpan(65526, 20).ToArray(), buf);
    }

    [Fact]
    public void SeekFromEndAndCurrentWork()
    {
        using var s = Open();
        s.Seek(-100, SeekOrigin.End);
        var tail = new byte[100];
        s.ReadExactly(tail);
        Assert.Equal(Payload.AsSpan(Payload.Length - 100, 100).ToArray(), tail);

        s.Position = 0;
        s.Seek(50, SeekOrigin.Current);
        Assert.Equal(50, s.Position);
    }

    [Fact]
    public void ReadPastEndReturnsZero()
    {
        using var s = Open();
        s.Position = s.Length;
        Assert.Equal(0, s.Read(new byte[16], 0, 16));
    }

    [Fact]
    public void WritesAreRejected()
    {
        using var s = Open();
        Assert.Throws<NotSupportedException>(() => s.Write(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => s.SetLength(0));
    }

    [Fact]
    public void ConcurrentReadersOverOneSourceAgreeWithSequential()
    {
        var r = new ExfatReader(new TestVolume().Add("data/big.bin", Payload).Build());
        var entry = r.EnumerateFiles().Single();
        string expected = Convert.ToHexString(SHA256.HashData(Payload));

        Parallel.For(0, 8, _ =>
        {
            using var s = r.OpenFile(entry);
            using var ms = new MemoryStream();
            s.CopyTo(ms, 8192);
            Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(ms.ToArray())));
        });
    }
}

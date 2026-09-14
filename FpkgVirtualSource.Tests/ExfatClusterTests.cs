using Xunit;

namespace FpkgVirtualSource.Tests;

public class ExfatClusterTests
{
    // 512-byte sectors, 128 sectors/cluster => 64 KiB clusters. FAT at sector 128.
    private const int ClusterSize = 65536;
    private const long FatByteOffset = 128 * 512;

    private static ByteSource Volume(params (int cluster, uint next)[] fat)
    {
        var bytes = new byte[4 * 1024 * 1024];
        BootSector.Build().CopyTo(bytes, 0);
        foreach (var (cluster, next) in fat)
            BitConverter.GetBytes(next).CopyTo(bytes, FatByteOffset + cluster * 4);
        return new ByteSource(bytes);
    }

    [Fact]
    public void ContiguousAllocationNeedsNoFat()
    {
        var r = new ExfatReader(Volume());
        Assert.Equal(new[] { 10, 11, 12 },
            r.IterateClusters(10, noFatChain: true, length: 3 * ClusterSize).ToArray());
    }

    [Fact]
    public void ContiguousAllocationRoundsPartialClusterUp()
    {
        var r = new ExfatReader(Volume());
        Assert.Equal(new[] { 10, 11 },
            r.IterateClusters(10, noFatChain: true, length: ClusterSize + 1).ToArray());
    }

    [Fact]
    public void FollowsFatChainToEndMarker()
    {
        var r = new ExfatReader(Volume((10, 11), (11, 20), (20, 0xFFFFFFFF)));
        Assert.Equal(new[] { 10, 11, 20 },
            r.IterateClusters(10, noFatChain: false, length: 0).ToArray());
    }

    [Fact]
    public void StopsAtRecordedLengthEvenIfChainContinues()
    {
        var r = new ExfatReader(Volume((10, 11), (11, 20), (20, 0xFFFFFFFF)));
        Assert.Equal(new[] { 10, 11 },
            r.IterateClusters(10, noFatChain: false, length: 2 * ClusterSize).ToArray());
    }

    [Fact]
    public void EmptyAllocationYieldsNothing()
    {
        var r = new ExfatReader(Volume());
        Assert.Empty(r.IterateClusters(0, noFatChain: false, length: 0));
        Assert.Empty(r.IterateClusters(10, noFatChain: true, length: 0));
    }

    [Fact]
    public void DetectsChainLoop()
    {
        // 10 -> 11 -> 10 -> ... with no length bound.
        var r = new ExfatReader(Volume((10, 11), (11, 10)));
        Assert.Throws<ExfatException>(() =>
            r.IterateClusters(10, noFatChain: false, length: 0).ToArray());
    }

    [Fact]
    public void ClusterOffsetIsHeapRelativeFromClusterTwo()
    {
        var r = new ExfatReader(Volume());
        // heap at sector 256, 512-byte sectors, 64 KiB clusters; cluster 2 is the first.
        Assert.Equal(256L * 512, r.ClusterByteOffset(2));
        Assert.Equal(256L * 512 + ClusterSize, r.ClusterByteOffset(3));
    }
}

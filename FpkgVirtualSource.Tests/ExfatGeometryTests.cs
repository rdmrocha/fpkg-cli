using LibProsperoPkg.Util;
using Xunit;

namespace FpkgVirtualSource.Tests;

public class ExfatGeometryTests
{
    private static ExfatGeometry Parse(byte[] vbr) => ExfatGeometry.Parse(new ByteSource(vbr));

    [Fact]
    public void ParsesShiftsIntoSizes()
    {
        var g = Parse(BootSector.Build(bytesPerSectorShift: 9, sectorsPerClusterShift: 7));
        Assert.Equal(512, g.BytesPerSector);
        Assert.Equal(128, g.SectorsPerCluster);
        Assert.Equal(65536, g.ClusterSize);
    }

    [Fact]
    public void ParsesLayoutFields()
    {
        var g = Parse(BootSector.Build(fatOffsetSectors: 128, heapOffsetSectors: 256,
                                       clusterCount: 1024, rootCluster: 5, serial: 0xDEADBEEF));
        Assert.Equal(128, g.FatOffsetSectors);
        Assert.Equal(256, g.ClusterHeapOffsetSectors);
        Assert.Equal(1024, g.ClusterCount);
        Assert.Equal(5, g.RootDirCluster);
        Assert.Equal(0xDEADBEEFu, g.VolumeSerial);
    }

    [Fact]
    public void RejectsMissingSignature()
    {
        var v = BootSector.Build();
        v[3] = (byte)'X';
        Assert.Throws<ExfatException>(() => Parse(v));
    }

    [Fact]
    public void RejectsMissingBootSignature()
    {
        var v = BootSector.Build();
        v[0x1FE] = 0;
        Assert.Throws<ExfatException>(() => Parse(v));
    }

    [Theory]
    [InlineData((byte)8)]
    [InlineData((byte)13)]
    public void RejectsUnsupportedSectorShift(byte shift)
    {
        Assert.Throws<ExfatException>(() => Parse(BootSector.Build(bytesPerSectorShift: shift)));
    }

    [Fact]
    public void RejectsClusterCountAboveIntMaxValue()
    {
        Assert.Throws<ExfatException>(() => Parse(BootSector.Build(clusterCount: uint.MaxValue)));
    }

    [Fact]
    public void RejectsRootDirClusterAboveIntMaxValue()
    {
        Assert.Throws<ExfatException>(() => Parse(BootSector.Build(rootCluster: uint.MaxValue)));
    }
}

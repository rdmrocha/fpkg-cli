using LibProsperoPkg.Util;

namespace FpkgVirtualSource.Tests;

/// <summary>An IMemoryReader over a byte array, for tests.</summary>
internal sealed class ByteSource(byte[] bytes) : IMemoryReader
{
    public byte[] Bytes { get; } = bytes;
    public void Read(long pos, byte[] buf, int offset, int count) =>
        Array.Copy(Bytes, pos, buf, offset, count);
    public void Dispose() { }
}

internal static class BootSector
{
    /// <summary>A 512-byte exFAT main boot sector with the given geometry.</summary>
    internal static byte[] Build(
        uint fatOffsetSectors = 128, uint fatLengthSectors = 64,
        uint heapOffsetSectors = 256, uint clusterCount = 1024,
        uint rootCluster = 2, uint serial = 0xDEADBEEF,
        byte bytesPerSectorShift = 9, byte sectorsPerClusterShift = 7)
    {
        var v = new byte[512];
        "EXFAT   "u8.CopyTo(v.AsSpan(3, 8));
        BitConverter.GetBytes(fatOffsetSectors).CopyTo(v, 0x50);
        BitConverter.GetBytes(fatLengthSectors).CopyTo(v, 0x54);
        BitConverter.GetBytes(heapOffsetSectors).CopyTo(v, 0x58);
        BitConverter.GetBytes(clusterCount).CopyTo(v, 0x5C);
        BitConverter.GetBytes(rootCluster).CopyTo(v, 0x60);
        BitConverter.GetBytes(serial).CopyTo(v, 0x64);
        v[0x6C] = bytesPerSectorShift;
        v[0x6D] = sectorsPerClusterShift;
        v[0x1FE] = 0x55; v[0x1FF] = 0xAA;
        return v;
    }
}

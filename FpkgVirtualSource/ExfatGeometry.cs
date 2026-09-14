using System.Buffers.Binary;
using LibProsperoPkg.Util;

namespace FpkgVirtualSource;

/// <summary>Thrown when a volume is malformed or uses exFAT features this reader does not cover.</summary>
public sealed class ExfatException(string message) : Exception(message);

/// <summary>Volume geometry from the exFAT main boot sector.</summary>
public sealed record ExfatGeometry(
    int BytesPerSector,
    int SectorsPerCluster,
    long FatOffsetSectors,
    long ClusterHeapOffsetSectors,
    int ClusterCount,
    int RootDirCluster,
    uint VolumeSerial)
{
    public long ClusterSize => (long)BytesPerSector * SectorsPerCluster;

    private static ReadOnlySpan<byte> Signature => "EXFAT   "u8;

    public static ExfatGeometry Parse(IMemoryReader source)
    {
        var vbr = new byte[512];
        try { source.Read(0, vbr, 0, vbr.Length); }
        catch (Exception ex) { throw new ExfatException("source too small for an exFAT boot sector: " + ex.Message); }

        if (!vbr.AsSpan(3, 8).SequenceEqual(Signature))
            throw new ExfatException("missing exFAT file system signature");
        if (BinaryPrimitives.ReadUInt16LittleEndian(vbr.AsSpan(0x1FE)) != 0xAA55)
            throw new ExfatException("missing boot signature 0xAA55");

        byte bytesPerSectorShift = vbr[0x6C];
        byte sectorsPerClusterShift = vbr[0x6D];
        if (bytesPerSectorShift is < 9 or > 12)
            throw new ExfatException($"unsupported bytes-per-sector shift {bytesPerSectorShift}");
        if (sectorsPerClusterShift > 25 - bytesPerSectorShift)
            throw new ExfatException($"unsupported sectors-per-cluster shift {sectorsPerClusterShift}");

        uint clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(vbr.AsSpan(0x5C));
        if (clusterCount > int.MaxValue)
            throw new ExfatException($"cluster count {clusterCount} exceeds the supported maximum");
        uint rootDirCluster = BinaryPrimitives.ReadUInt32LittleEndian(vbr.AsSpan(0x60));
        if (rootDirCluster > int.MaxValue)
            throw new ExfatException($"root directory cluster {rootDirCluster} exceeds the supported maximum");

        return new ExfatGeometry(
            BytesPerSector: 1 << bytesPerSectorShift,
            SectorsPerCluster: 1 << sectorsPerClusterShift,
            FatOffsetSectors: BinaryPrimitives.ReadUInt32LittleEndian(vbr.AsSpan(0x50)),
            ClusterHeapOffsetSectors: BinaryPrimitives.ReadUInt32LittleEndian(vbr.AsSpan(0x58)),
            ClusterCount: (int)clusterCount,
            RootDirCluster: (int)rootDirCluster,
            VolumeSerial: BinaryPrimitives.ReadUInt32LittleEndian(vbr.AsSpan(0x64)));
    }
}

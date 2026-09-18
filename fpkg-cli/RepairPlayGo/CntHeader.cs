using System.Buffers.Binary;

namespace Fpkg.Cli.RepairPlayGo;

/// <summary>
/// Named big-endian field offsets into the CNT header, with typed accessors. Offsets come from
/// <c>PkgWriter.WriteHeader</c> in the shipped 0.6.9 LibProsperoPkg assembly. Offsets and
/// accessors only — no business logic.
/// </summary>
internal static class CntHeader
{
    internal const int EntryCount         = 16;   // u32
    internal const int ScEntryCount       = 20;   // u16
    internal const int EntryCount2        = 22;   // u16
    internal const int EntryTableOffset   = 24;   // u32
    internal const int MainEntDataSize    = 28;   // u32
    internal const int BodyOffset         = 32;   // u64
    internal const int BodySize           = 40;   // u64
    internal const int MandatorySize      = 48;   // u64
    internal const int ContentId          = 64;   // 48-byte ASCII slot
    internal const int DrmType            = 112;  // u32
    internal const int ContentType        = 116;  // u32
    internal const int ScEntries1Hash     = 256;  // 32 B
    internal const int ScEntries2Hash     = 288;  // 32 B
    internal const int DigestTableHash    = 320;  // 32 B
    internal const int BodyDigest         = 352;  // 32 B
    internal const int MountDescriptor    = 1024; // 128 B  (the ForceFihRelativeImageOffset input)
    internal const int PfsImageOffset     = 1040; // u64    (= MountDescriptor + 0x10)
    internal const int PfsImageDigest     = 1088; // 32 B
    internal const int CntRegionOffset    = 1200; // u64
    internal const int CntRegionSize      = 1208; // u64
    internal const int DescImageKeyOffset = 1296; // u32
    internal const int DescImageKeySize   = 1300; // u32
    internal const int DescMandatoryOffset= 1304; // u32
    internal const int DescMandatorySize  = 1308; // u32
    internal const int DescDigest         = 1312; // 64 B
    internal const int PackageDigest      = 4064; // 32 B
    internal const int HeaderWrap         = 4096;

    internal static ushort U16(ReadOnlySpan<byte> cnt, int off) => BinaryPrimitives.ReadUInt16BigEndian(cnt[off..]);
    internal static uint   U32(ReadOnlySpan<byte> cnt, int off) => BinaryPrimitives.ReadUInt32BigEndian(cnt[off..]);
    internal static ulong  U64(ReadOnlySpan<byte> cnt, int off) => BinaryPrimitives.ReadUInt64BigEndian(cnt[off..]);
    internal static void SetU32(Span<byte> cnt, int off, uint v)  => BinaryPrimitives.WriteUInt32BigEndian(cnt[off..], v);
    internal static void SetU64(Span<byte> cnt, int off, ulong v) => BinaryPrimitives.WriteUInt64BigEndian(cnt[off..], v);
    internal static void SetBytes(Span<byte> cnt, int off, ReadOnlySpan<byte> v) => v.CopyTo(cnt[off..]);
}

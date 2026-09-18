using System.Buffers.Binary;
using System.Text;
using Fpkg.Cli.RepairPlayGo;
using LibProsperoPkg.PKG;
using Xunit;

public class CntHeaderTests
{
    [SkippableFact]
    public void OffsetsAgreeWithTheLibrarysOwnReader()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var cnt = PackageRegions.Load(TestPackage.Path).Cnt;
        var hdr = ProsperoPkgReader.Read(TestPackage.Path).Header;

        Assert.Equal(hdr.EntryCount,       CntHeader.U32(cnt, CntHeader.EntryCount));
        Assert.Equal(hdr.ScEntryCount,     CntHeader.U16(cnt, CntHeader.ScEntryCount));
        Assert.Equal(hdr.EntryTableOffset, CntHeader.U32(cnt, CntHeader.EntryTableOffset));
        Assert.Equal(hdr.BodyOffset,       CntHeader.U64(cnt, CntHeader.BodyOffset));
        Assert.Equal(hdr.BodySize,         CntHeader.U64(cnt, CntHeader.BodySize));
        Assert.Equal(hdr.ContentType,      CntHeader.U32(cnt, CntHeader.ContentType));
        Assert.Equal(hdr.ContentId,
            Encoding.ASCII.GetString(cnt, CntHeader.ContentId, 36).TrimEnd('\0'));
    }

    [SkippableFact]
    public void KnownConstantsForTheTestPackage()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var cnt = PackageRegions.Load(TestPackage.Path).Cnt;
        Assert.Equal(27u,        CntHeader.U32(cnt, CntHeader.EntryCount));
        Assert.Equal((ushort)6,  CntHeader.U16(cnt, CntHeader.ScEntryCount));
        Assert.Equal(0x2000ul,   CntHeader.U64(cnt, CntHeader.BodyOffset));
        Assert.Equal(0x3C9E000ul,CntHeader.U64(cnt, CntHeader.BodySize));
        Assert.Equal(7200u,      CntHeader.U32(cnt, CntHeader.MainEntDataSize));
        // IMAGEDIGS_DAT (1034) sits at 8,399,200 and IMAGE_KEY (32) at 11,136.
        Assert.Equal(8_399_200u, CntHeader.U32(cnt, CntHeader.DescMandatoryOffset));
        Assert.Equal(291_360u,   CntHeader.U32(cnt, CntHeader.DescMandatorySize));
        Assert.Equal(11_136u,    CntHeader.U32(cnt, CntHeader.DescImageKeyOffset));
        Assert.Equal(2048u,      CntHeader.U32(cnt, CntHeader.DescImageKeySize));
        Assert.Equal(8_399_200ul,CntHeader.U64(cnt, CntHeader.MandatorySize));

        var hdr = ProsperoPkgReader.Read(TestPackage.Path).Header;
        Assert.Equal(hdr.DrmType, CntHeader.U32(cnt, CntHeader.DrmType));

        // ProsperoPkgHeader has no PfsImageDigest counterpart, so this only proves the offset
        // lands inside a real digest (32 non-zero bytes) rather than proving the exact value.
        Assert.Contains(cnt.AsSpan(CntHeader.PfsImageDigest, 32).ToArray(), b => b != 0);
    }

    [SkippableFact]
    public void ThePackageDigestAtOffset4064IsWhatTheLibraryComputes()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var cnt = PackageRegions.Load(TestPackage.Path).Cnt;
        var preimage = cnt[..4064];
        BinaryPrimitives.WriteUInt64BigEndian(preimage.AsSpan(CntHeader.PfsImageOffset, 8), 65536ul);
        Assert.Equal(
            Convert.ToHexString(ProsperoImageDigests.ComputePackageDigest(preimage)),
            Convert.ToHexString(cnt.AsSpan(CntHeader.PackageDigest, 32)));
    }
}

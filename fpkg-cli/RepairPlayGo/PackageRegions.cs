using LibProsperoPkg.PKG;

namespace Fpkg.Cli.RepairPlayGo;

/// <summary>
/// A finalized .pkg split into its three regions: the outer PFS (never materialised — only its
/// offset/size are kept), the embedded CNT container, and the trailing SI segment (empty when the
/// package has none). <see cref="Load"/> discovers the boundaries; <see cref="WriteTo"/> splices
/// a (possibly modified) CNT and SI back onto the original outer PFS bytes without ever holding
/// the ~600 MB payload in memory.
/// </summary>
internal sealed record PackageRegions(
    string SourcePath,
    long OuterPfsOffset,   // 0x10000
    long OuterPfsSize,
    long CntOffset,        // = OuterPfsOffset + OuterPfsSize
    byte[] Cnt,
    byte[] Si)             // empty array when the package has no SI segment
{
    /// <summary>
    /// Reads the FIH to find the embedded CNT offset, then uses
    /// <see cref="ProsperoPackageArchive.Split"/> to pull out the CNT and SI regions. The outer
    /// PFS is discarded into <see cref="Stream.Null"/> rather than buffered — at ~600 MB it is by
    /// far the largest of the three regions and <see cref="WriteTo"/> only ever needs to copy it
    /// byte for byte from the source file, never decode it.
    /// </summary>
    internal static PackageRegions Load(string packagePath)
    {
        var pkg = ProsperoPkgReader.Read(packagePath);
        long cntOffset = (long)pkg.Fih.EmbeddedCntOffset;

        using var input = File.OpenRead(packagePath);
        using var cnt = new MemoryStream();
        using var si = new MemoryStream();
        ProsperoPackageArchive.Split(input, Stream.Null, cnt, si);

        return new PackageRegions(
            packagePath,
            OuterPfsOffset: 0x10000,
            OuterPfsSize: cntOffset - 0x10000,
            CntOffset: cntOffset,
            Cnt: cnt.ToArray(),
            Si: si.ToArray());
    }

    /// <summary>
    /// Streams <c>[0, CntOffset)</c> — the header, FIH and the whole outer PFS — verbatim from
    /// the source file, then writes <paramref name="cnt"/> and <paramref name="si"/>. Never reads
    /// the outer PFS into memory.
    /// </summary>
    internal void WriteTo(string outputPath, byte[] cnt, byte[] si)
    {
        using var src = File.OpenRead(SourcePath);
        using var dst = File.Create(outputPath);
        // Header, FIH and the whole outer PFS, byte for byte. Never decoded, never rewritten.
        var head = new byte[81920];
        long remaining = CntOffset;
        while (remaining > 0)
        {
            int n = src.Read(head, 0, (int)Math.Min(head.Length, remaining));
            if (n <= 0) throw new EndOfStreamException("package truncated before the CNT region");
            dst.Write(head, 0, n);
            remaining -= n;
        }
        dst.Write(cnt);
        dst.Write(si);
    }
}

using LibProsperoPkg.PKG;

namespace Fpkg.Cli.RepairPlayGo;

/// <summary>
/// A finalized .pkg split into its three regions: the outer PFS (never materialised — only its
/// offset/size are kept), the embedded CNT container, and the trailing SI segment (empty when the
/// package has none). <see cref="Load"/> discovers the boundaries and reads only those two small
/// regions; <see cref="WriteTo"/> splices a (possibly modified) CNT and SI back onto the original
/// outer PFS bytes without ever holding the ~600 MB payload in memory.
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
    /// Reads the FIH to confirm the package is finalized, asks
    /// <see cref="ProsperoPackageArchive.Inspect"/> for the region geometry, then reads ONLY the
    /// CNT and SI ranges back with explicit seeks.
    /// <para>
    /// It deliberately does NOT use <c>ProsperoPackageArchive.Split</c>. Split copies the outer
    /// PFS too, and passing <see cref="Stream.Null"/> as its destination discards the writes but
    /// not the reads — it still pulls the whole ~600 MB payload (on a 90 GB package, 90 GB) off
    /// disk to throw it away, on every run including <c>--dry-run</c>. The outer PFS is never
    /// decoded here and <see cref="WriteTo"/> copies it byte for byte straight from the source
    /// file, so nothing needs to read it at load time.
    /// </para>
    /// </summary>
    internal static PackageRegions Load(string packagePath, Progress? progress = null) =>
        Load(packagePath, progress, static path => File.OpenRead(path));

    /// <summary>
    /// The body of <see cref="Load"/> with the source stream's construction injected, so a test can
    /// interpose a byte-counting stream and assert that the payload is not read. Production code
    /// calls the two-argument overload.
    /// </summary>
    internal static PackageRegions Load(string packagePath, Progress? progress,
                                        Func<string, Stream> openSourceStream)
    {
        var pkg = ProsperoPkgReader.Read(packagePath);
        if (pkg.Fih is null)
            throw new InvalidDataException(
                "the file has no FIH header, so it carries no embedded CNT region; repair-playgo needs a finalized package");

        using var input = openSourceStream(packagePath);
        var map = ProsperoPackageArchive.Inspect(input);

        // The stage's real work is exactly these two ranges, so that — not the file position, which
        // starts at ~99.9% of a large package the moment we seek to the CNT — is what drives the bar.
        long expected = map.CntSize + map.SupplementSize;
        long done = 0;
        byte[] cnt = ReadRange(input, map.CntOffset, map.CntSize, "CNT", progress, ref done, expected);
        byte[] si = ReadRange(input, map.SupplementOffset, map.SupplementSize, "SI", progress, ref done, expected);

        return new PackageRegions(
            packagePath,
            OuterPfsOffset: map.OuterPfsOffset,
            OuterPfsSize: map.OuterPfsSize,
            CntOffset: map.CntOffset,
            Cnt: cnt,
            Si: si);
    }

    /// <summary>
    /// Reads <paramref name="size"/> bytes from <paramref name="offset"/> in 80 KiB steps, counting
    /// them towards the stage's own total. A zero-length region (a package with no SI) reads nothing.
    /// </summary>
    private static byte[] ReadRange(Stream input, long offset, long size, string what,
                                    Progress? progress, ref long done, long expected)
    {
        if (size <= 0)
            return [];

        var buffer = new byte[checked((int)size)];
        input.Seek(offset, SeekOrigin.Begin);
        int filled = 0;
        while (filled < buffer.Length)
        {
            int n = input.Read(buffer, filled, Math.Min(81920, buffer.Length - filled));
            if (n <= 0)
                throw new EndOfStreamException($"package truncated inside the {what} region");
            filled += n;
            done += n;
            progress?.Report(done, expected);
        }
        return buffer;
    }

    /// <summary>
    /// Streams <c>[0, CntOffset)</c> — the header, FIH and the whole outer PFS — verbatim from
    /// the source file, then writes <paramref name="cnt"/> and <paramref name="si"/>. Never reads
    /// the outer PFS into memory.
    /// <para>
    /// <paramref name="flushToDisk"/> defaults to true — the safe behaviour every caller had
    /// before it existed. Pass false only for an INTERMEDIATE pass whose bytes are read back by
    /// this same process and then overwritten: an fsync of ~630 MB is the single most expensive
    /// thing this command does, and the page cache serves an in-process read-back just as well.
    /// The pass whose output is about to be renamed over the target must keep it.
    /// </para>
    /// </summary>
    internal void WriteTo(string outputPath, byte[] cnt, byte[] si, bool flushToDisk = true,
                          Progress? progress = null)
    {
        using var src = File.OpenRead(SourcePath);
        using var dst = File.Create(outputPath);
        // Header, FIH and the whole outer PFS, byte for byte. Never decoded, never rewritten.
        var buffer = new byte[81920];
        long total = CntOffset + cnt.Length + si.Length;
        long remaining = CntOffset;
        while (remaining > 0)
        {
            int n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (n <= 0) throw new EndOfStreamException("package truncated before the CNT region");
            dst.Write(buffer, 0, n);
            remaining -= n;
            progress?.Report(CntOffset - remaining, total);
        }
        dst.Write(cnt);
        dst.Write(si);
        progress?.Report(total, total);
        // Flushed to the DEVICE, not just out of the managed buffer. The caller renames this file
        // over the target, and the only data-loss window in that sequence is a power loss straight
        // after the rename leaving a renamed-but-incomplete file. Committing the bytes first
        // closes it. An intermediate pass has no such window and opts out.
        dst.Flush(flushToDisk);
    }
}

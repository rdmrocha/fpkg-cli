using LibProsperoPkg.PKG;

namespace Fpkg.Cli.PlayGo;

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

        // CROSS-CHECKED, not merely taken from one of the two. CntOffset is the value WriteTo uses
        // as the verbatim copy length: everything below it is streamed from the source byte for
        // byte and everything at or above it is replaced by the rebuilt CNT and SI. If the FIH and
        // the container map disagree about where that boundary is, one of them describes a
        // different package layout than the one being spliced, and the output would be silently
        // wrong — truncated or overlapping — with every digest recomputed over the wrong bytes.
        // On an --in-place run that file replaces the original, so this has to be a refusal, not a
        // preference for whichever source happens to be consulted.
        //
        // UNTESTED, deliberately, and this records why. The FIH stores the value at file offset
        // 0x58 and Inspect derives its own as OuterPfsOffset + OuterPfsSize, so doctoring 0x58 in a
        // copy is the obvious fixture — but ProsperoPkgReader.Read above checks for the CNT magic
        // at the FIH's offset and fails first with "Embedded container has invalid CNT magic"
        // (measured, not assumed). Reaching this line from a doctored file would mean planting a
        // second valid CNT header at the doctored offset, which is an invented package rather than
        // a doctored one. The guard stays because the reader's magic check is not the same check:
        // it proves something CNT-shaped is there, not that it is the CNT Inspect measured.
        if (map.CntOffset != (long)pkg.Fih.EmbeddedCntOffset)
            throw new InvalidDataException(
                $"the package's FIH says the embedded CNT begins at {(long)pkg.Fih.EmbeddedCntOffset} " +
                $"but its container map says {map.CntOffset}. The FIH and the container map disagree " +
                "about where the CNT begins, so this package cannot be repaired safely.");

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

        // Named, not a bare OverflowException from checked((int)size). The whole region is held in
        // memory as one array, so a >2 GB CNT cannot be loaded at all — and huge packages are
        // exactly this command's subject, so the message has to say which region and how large.
        if (size > int.MaxValue)
            throw new InvalidDataException(
                $"the {what} region is {size:N0} bytes, larger than the 2 GiB a single buffer can " +
                $"hold; this package's {what} is too large to load into memory.");

        var buffer = new byte[(int)size];
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
    /// <param name="openDestination">
    /// How the destination file is created. Null means <see cref="File.Create(string)"/>, which is
    /// what production uses; a test passes a byte-counting stream through it to assert how much
    /// this command actually writes.
    /// </param>
    internal void WriteTo(string outputPath, byte[] cnt, byte[] si, bool flushToDisk = true,
                          Progress? progress = null, Func<string, Stream>? openDestination = null)
    {
        using var src = File.OpenRead(SourcePath);
        using var dst = openDestination is null ? File.Create(outputPath) : openDestination(outputPath);
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
        FlushStaged(dst, flushToDisk);
    }

    /// <summary>
    /// <c>Flush(bool)</c> — the one that reaches the DEVICE — exists on <see cref="FileStream"/>,
    /// not on <see cref="Stream"/>, and the destination is a <see cref="Stream"/> so that a test
    /// can interpose. A wrapper is asked to flush its own buffers; only a real file can be fsynced.
    /// </summary>
    private static void FlushStaged(Stream dst, bool flushToDisk)
    {
        if (dst is FileStream file) file.Flush(flushToDisk);
        else dst.Flush();
    }

    /// <summary>
    /// Replaces ONLY the trailing SI of a file <see cref="WriteTo"/> has already staged, by
    /// seeking to the end of the CNT and writing the new segment there. The payload and the CNT
    /// below <c>CntOffset + cnt.Length</c> are left exactly as they are.
    /// <para>
    /// This is why the staged (<c>--out</c>) path writes the package once rather than twice.
    /// <see cref="WriteTo"/> begins with a create-and-truncate, so calling it a second time to
    /// swap a ~660 KB SI rewrote the entire payload again — on the 90 GB package a user reported,
    /// ~180 GB written for 90 GB of output, visible as the temporary file growing to 80 GB,
    /// vanishing and regrowing. <see cref="InPlaceWriter"/> already used this seek-and-truncate
    /// shape; the staged writer now does too.
    /// </para>
    /// <para>
    /// No journal is involved and none is needed: the file being edited is the staging temporary,
    /// which does not yet exist as far as the user is concerned, and the target is untouched until
    /// the caller's <c>File.Move</c>.
    /// </para>
    /// </summary>
    /// <param name="openStaged">
    /// How the staged file is reopened. Null means <c>FileMode.Open, FileAccess.ReadWrite</c>,
    /// which is what production uses; a test passes a byte-counting stream through it.
    /// </param>
    internal void OverwriteSi(string outputPath, long cntLength, byte[] si,
                              Progress? progress = null, Func<string, Stream>? openStaged = null)
    {
        long siOffset = CntOffset + cntLength;
        using var dst = openStaged is null
            ? new FileStream(outputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            : openStaged(outputPath);
        dst.Seek(siOffset, SeekOrigin.Begin);
        dst.Write(si, 0, si.Length);
        // Cut to the new end: the rebuilt SI is normally shorter than the one pass 1 left room for
        // (pass 1 writes none at all), and anything beyond it is not part of the package.
        dst.SetLength(siOffset + si.LongLength);
        progress?.Report(si.Length, si.Length);
        // This is the pass whose output is renamed over the target, so it keeps the fsync.
        FlushStaged(dst, flushToDisk: true);
    }
}

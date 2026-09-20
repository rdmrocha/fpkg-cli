using System.Collections;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Security.Cryptography;
using LibProsperoPkg.PFS;
using LibProsperoPkg.PKG;

namespace Fpkg.Cli;

/// <summary>
/// Lists the inner-PFS files of a package — path, uncompressed size, and optionally a SHA-256 of
/// the file's DECODED contents — so two packages built from the same source by different tools can
/// be compared without unpacking either.
///
/// <para>
/// Nothing is written to disk and nothing is buffered. The package is opened as a chain of streams
/// (FileStream -> decrypted outer PFS -> NAPS logical stream -> inner PfsReader); each inner file
/// is then streamed through <see cref="PfsReader.File.CopyTo(Stream, bool)"/> into
/// <see cref="HashSink"/>, which feeds an incremental hash and discards the bytes. Peak memory is
/// a few inode-sized buffers whatever the file's size, which is the point: the packages this
/// exists for are ~88 GB and contain single inner files of tens of gigabytes.
/// </para>
/// </summary>
internal static class InnerList
{
    /// <param name="Size">The file's UNCOMPRESSED (logical) size — what a consumer of the file
    /// sees, and the number of bytes the SHA-256 covers.</param>
    /// <param name="PhysicalSize">The bytes the file occupies inside the PFS. Equal to
    /// <paramref name="Size"/> for stored files, smaller for compressed ones.</param>
    /// <param name="InnerOffset">Where the file's stored bytes begin in the inner LOGICAL PFS
    /// image (origin: byte 0 of the decoded inner image), or -1 when the file is fragmented.</param>
    /// <param name="PkgStart">Where those same bytes begin in the .pkg FILE — origin byte 0 of the
    /// package, so the 0x10000 FIH block is inside the origin, which is the coordinate space the
    /// PlayGo extent table uses. -1 when no range exists; see <paramref name="MapNote"/>.</param>
    /// <param name="PkgSpanStart">The .pkg range of the NAPS spans covering this file — the bytes
    /// a downloader needs for it. Available even when the data is compressed, and a superset:
    /// the end spans usually hold a neighbouring file's bytes too.</param>
    internal readonly record struct Entry(string Path, long Size, long PhysicalSize,
                                          bool Compressed, string? Sha256,
                                          long InnerOffset, long PkgStart, long PkgEnd,
                                          string? MapNote,
                                          long PkgSpanStart, long PkgSpanEnd, string? SpanNote);

    /// <summary>
    /// Yields every inner file lazily, in path order, with its decoded digest when
    /// <paramref name="sha256"/> is set. Lazy on purpose: a caller printing as it goes then holds
    /// one entry rather than the whole listing, and a <paramref name="filter"/> that excludes a
    /// file also skips hashing it.
    ///
    /// <para>
    /// Single-threaded on purpose too: the library's own extractor runs one session per worker,
    /// and several concurrent Kraken streams over an 88 GB package is exactly the memory profile
    /// this tool exists to avoid.
    /// </para>
    /// </summary>
    /// <param name="range">When given, only files whose .pkg byte range intersects
    /// [Start, End) are listed. A file with no mappable range never intersects, so it is dropped:
    /// use the unfiltered listing to see the ones that could not be placed.</param>
    /// <param name="untestable">Incremented once per file that a <paramref name="range"/> query
    /// could not test because it has no mappable footprint. A caller reporting a NEGATIVE result
    /// ("nothing is in this window") must surface this: an empty result with a non-zero count is
    /// not evidence of absence.</param>
    internal static IEnumerable<Entry> Enumerate(string packagePath, string passcode, bool sha256,
                                                 string? filter = null,
                                                 bool offsets = false,
                                                 (long Start, long End)? range = null,
                                                 CancellationToken cancellationToken = default,
                                                 StrongBox<long>? untestable = null)
    {
        if (range is not null) offsets = true;
        using var session = OpenSession(packagePath, passcode, cancellationToken);
        using var mapper = offsets
            ? new InnerOffsets(packagePath, passcode, PlanOf(session), cancellationToken)
            : null;
        // Only names are materialised here, never contents: sorting the listing costs one string
        // per inner file, which is bounded by the inode count, not by the package's size.
        foreach (var file in FilesOf(session).OrderBy(f => Normalize(f.FullName), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Normalize(file.FullName);
            if (filter is not null && !path.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            var placed = mapper?.Locate(file);
            var footprint = mapper?.Footprint(file);
            // The footprint is what a range query must test: it is the only one available for a
            // compressed file, and it is the superset, so a file is never silently excluded from
            // a window its bytes actually touch.
            if (range is { } r)
            {
                if (footprint is not { Mapped: true } m)
                {
                    // Not "no overlap" — "cannot say". Counted so a caller can distinguish an
                    // empty window from an untested one.
                    if (untestable is not null) untestable.Value++;
                    continue;
                }
                if (!(m.Start < r.End && m.End > r.Start)) continue;
            }
            yield return Describe(file, path, sha256, placed, footprint, cancellationToken);
        }
    }

    /// <summary>The package's three-hop layout plus each file's raw inode offset, for checking
    /// the <see cref="InnerOffsets"/> arithmetic by hand against a package.</summary>
    internal static string DescribeLayout(string packagePath, string passcode, long spansFrom = -1)
    {
        using var session = OpenSession(packagePath, passcode, CancellationToken.None);
        using var mapper = new InnerOffsets(packagePath, passcode, PlanOf(session), CancellationToken.None);
        var lines = new List<string> { mapper.DescribeLayout(spansFrom) };
        foreach (var f in FilesOf(session).OrderBy(f => f.offset))
            lines.Add($"  inode {f.ino} ulog {f.offset} size {f.size} " +
                      $"{(f.blocks is null ? "contiguous" : $"{f.blocks.Length} blocks")} " +
                      Normalize(f.FullName));
        return string.Join('\n', lines);
    }

    internal static Entry Describe(PfsReader.File file, string path, bool sha256,
                                   InnerOffsets.Range? placed, InnerOffsets.Range? footprint,
                                   CancellationToken cancellationToken)
    {
        bool compressed = file.flags.HasFlag(InodeFlags.compressed);
        // The dinode's two size fields are named the other way round from how they are used:
        // `size` is the PHYSICAL extent (the PFSC container for a compressed file) and
        // `compressed_size` is the LOGICAL size CopyTo(decompress: true) produces. PfsReader
        // itself relies on that — it throws when a Kraken unpack does not yield exactly
        // `compressed_size` bytes. Taking the fields at their names would report a compressed
        // file's size as its on-disk footprint, which is not what a cross-tool comparison wants.
        long logical = compressed ? file.compressed_size : file.size;
        long innerOffset = file.blocks is null ? file.offset : -1;
        long start = placed is { Mapped: true } p ? p.Start : -1;
        long end = placed is { Mapped: true } q ? q.End : -1;
        string? note = placed?.Note;

        string? digest = null;
        if (sha256)
        {
            long streamed;
            (digest, streamed) = StreamDigest(file, cancellationToken);
            if (streamed != logical)
                throw new InvalidDataException(
                    $"{path}: decoded to {streamed:N0} bytes but the inode declares {logical:N0}. " +
                    "The inode's size fields no longer mean what inner-list assumes; re-derive it " +
                    "against the shipped library before trusting any listing.");
        }
        return new Entry(path, logical, file.size, compressed, digest, innerOffset, start, end, note,
                         footprint is { Mapped: true } f ? f.Start : -1,
                         footprint is { Mapped: true } g ? g.End : -1,
                         footprint?.Note);
    }

    /// <summary>
    /// The digest of one small file, read whole. Exists so a test can check
    /// <see cref="StreamDigest"/> against a path that does not stream — never call it on a file
    /// whose size you have not looked at first.
    /// </summary>
    internal static string DigestOfSmallFile(string packagePath, string passcode, string path)
    {
        using var session = OpenSession(packagePath, passcode, CancellationToken.None);
        var file = FilesOf(session).Single(f => Normalize(f.FullName) == path);
        return Convert.ToHexString(SHA256.HashData(file.ReadAllBytes(decompress: true))).ToLowerInvariant();
    }

    private static (string Digest, long Bytes) StreamDigest(PfsReader.File file,
                                                            CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sink = new HashSink(hash);
        file.CopyTo(sink, decompress: true, cancellationToken, progress: null);
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), sink.Length);
    }

    /// <summary>Strips the inner root so paths read the way the GP5 and the dump spell them.</summary>
    internal static string Normalize(string fullName)
    {
        string path = fullName.Replace('\\', '/').TrimStart('/');
        return path.StartsWith("uroot/", StringComparison.Ordinal) ? path["uroot/".Length..] : path;
    }

    /// <summary>
    /// ProsperoPackageArchive.OpenInnerPfsSession is private, and reimplementing it would mean
    /// redoing the outer decode and the NAPS plan — far more surface than this diagnostic is
    /// worth. Its session holds only streams and an inode table, and reads file data on demand,
    /// which is precisely the property needed. Every public alternative
    /// (DecodeInnerPfs/ExtractInnerFiles) either returns a byte[] of the whole image or writes
    /// files out, so neither can be used here.
    /// </summary>
    private static IDisposable OpenSession(string packagePath, string passcode,
                                           CancellationToken cancellationToken)
    {
        var method = typeof(ProsperoPackageArchive).GetMethod(
            "OpenInnerPfsSession", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "LibProsperoPkg no longer has ProsperoPackageArchive.OpenInnerPfsSession; " +
                "re-derive inner-list against the shipped library.");
        try
        {
            return (IDisposable)method.Invoke(null, [packagePath, passcode, cancellationToken])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static LibProsperoPkg.PFS.Compression.ProsperoNapsPlan PlanOf(object session) =>
        (LibProsperoPkg.PFS.Compression.ProsperoNapsPlan)(
            session.GetType().GetProperty("Plan")?.GetValue(session)
            ?? throw new InvalidOperationException(
                "the inner-PFS session no longer exposes Plan; re-derive inner-list's offsets."));

    private static IEnumerable<PfsReader.File> FilesOf(object session)
    {
        var files = session.GetType().GetProperty("Files")?.GetValue(session)
            ?? throw new InvalidOperationException(
                "the inner-PFS session no longer exposes Files; re-derive inner-list.");
        return ((IDictionary)files).Values.Cast<PfsReader.File>();
    }

    /// <summary>
    /// A write-only sink that hashes and discards. <c>CanSeek</c> is false deliberately: CopyTo
    /// calls <c>SetLength</c> on a seekable destination, which this cannot honour.
    /// </summary>
    private sealed class HashSink(IncrementalHash hash) : Stream
    {
        private long written;

        public override bool CanWrite => true;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            hash.AppendData(buffer, offset, count);
            written += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            hash.AppendData(buffer);
            written += buffer.Length;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

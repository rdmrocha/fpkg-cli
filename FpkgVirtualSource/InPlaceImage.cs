using System.Globalization;

namespace FpkgVirtualSource;

/// <summary>
/// Builds the package in one file instead of two.
///
/// <para>Stock LibProsperoPkg writes the inner <c>pfs_image.dat</c> to a spool in
/// <c>--temp-dir</c> (stage 1) and then reads it back through the outer-PFS writer, which encrypts
/// and hashes it into the final package at offset 0x10000 (stage 3). Both files are full size at
/// the same moment, so a build needs twice the package size in free space.</para>
///
/// <para>With this shim the spool path becomes a <c>fpkgwin:&lt;package&gt;@65536</c> window: stage 1
/// writes the plaintext image straight into the package's outer-PFS data region, and stage 3 reads
/// block <c>j</c> from the very offset it writes it back to. That is safe because the outer writer
/// consumes its source strictly sequentially, one 64 KiB block at a time, always ahead of its own
/// writes, and every structural block it emits is allocated after the data blocks.</para>
///
/// <para>Inert unless the CLI sets <see cref="Enabled"/>: every entry point returns its input
/// unchanged, so an unconfigured or unpatched build behaves exactly as before.</para>
/// </summary>
public static class InPlaceImage
{
    /// <summary>The scheme marking a path as a byte range of another file.</summary>
    public const string Scheme = "fpkgwin:";

    /// <summary>Package offset of the outer PFS, and so of outer data block 0.</summary>
    public const long OuterPfsOffset = 65536;

    private static WindowStream? lastWrite;
    private static string? windowPath;
    private static string? fallbackSpool;
    private static long? relocatedLength;

    /// <summary>Whether the CLI has switched the single-file build on.</summary>
    public static bool Enabled { get; set; }

    /// <summary>Ceiling on the displaced bytes an in-place layout relocation may hold in memory.</summary>
    public static long MaxRelocationBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Where a fallback spool goes when the in-place relocation will not fit. The CLI
    /// points this at <c>--temp-dir</c>.</summary>
    public static string FallbackDirectory { get; set; } = Path.GetTempPath();

    /// <summary>Where notices go — the build's normal output, because this changes where bytes land.</summary>
    public static Action<string>? Log { get; set; } = Console.Out.WriteLine;

    /// <summary>Why the in-place path gave up, if it did. Non-null means the build used a spool.</summary>
    public static string? FallbackReason { get; private set; }

    /// <summary>Clears per-build state. The CLI calls this before every build.</summary>
    public static void Reset()
    {
        Enabled = false;
        FallbackReason = null;
        MaxRelocationBytes = 512L * 1024 * 1024;
        FallbackDirectory = Path.GetTempPath();
        lastWrite = null;
        windowPath = null;
        fallbackSpool = null;
        relocatedLength = null;
    }

    /// <summary>
    /// Site 16. Redirects stage 1's spool to the final package's PFS data region — but only when
    /// the outer PFS is already prepositioned into the output
    /// (<c>PrepositionedFihOutputPath</c>), because otherwise stage 3 would not be writing where
    /// we put the plaintext.
    /// </summary>
    public static string SpoolPathFor(string spoolPath, string outputPath, bool prepositioned)
    {
        if (!Enabled || !prepositioned || string.IsNullOrWhiteSpace(outputPath))
            return spoolPath;

        windowPath = Scheme + Path.GetFullPath(outputPath) + "@" +
                     OuterPfsOffset.ToString(CultureInfo.InvariantCulture);
        fallbackSpool = null;
        relocatedLength = null;
        Log?.Invoke("Single-file build: the inner image is written directly into the package at " +
                    $"0x{OuterPfsOffset:X}; no spool copy is made.");
        return windowPath;
    }

    /// <summary>True for a path this shim owns.</summary>
    public static bool IsWindow(string? path) =>
        path is not null && path.StartsWith(Scheme, StringComparison.Ordinal);

    /// <summary>Site 17. <c>Path.GetFullPath</c> would mangle a window path into a real one.</summary>
    public static string GuardFullPath(string path) => IsWindow(path) ? path : Path.GetFullPath(path);

    /// <summary>Site 20. The layout permutation must be materialised, not deferred to the reader:
    /// a deferred layout would have the outer writer reading the image through a mapping of the
    /// very bytes it is overwriting.</summary>
    public static bool DeferLayout() => !Enabled || windowPath is null;

    /// <summary>
    /// Sites 18, 19, 22. A window path opens as a range of the package; anything else is a file.
    /// </summary>
    public static Stream Open(string path, FileMode mode, FileAccess access, FileShare share,
                              int bufferSize, FileOptions options)
    {
        if (!TryParse(path, out string file, out long offset))
            return new FileStream(path, mode, access, share, bufferSize, options);

        // Once the relocation has fallen back, the image lives in a real spool and the window is
        // only a name for it.
        if (fallbackSpool is not null && access == FileAccess.Read)
            return new LayoutRelocator.RecordingSource(
                new WindowStream(new FileStream(fallbackSpool, FileMode.Open, FileAccess.Read,
                                                FileShare.ReadWrite, bufferSize, options), 0, false))
                .Install();

        Stream stream = OpenWindow(file, offset, mode, access, bufferSize, options);
        // The relocation pass needs to see every read the library's layout stream performs, and
        // a recorder that is neither recording nor serving is a plain pass-through.
        return access == FileAccess.Read
            ? new LayoutRelocator.RecordingSource((WindowStream)stream).Install()
            : stream;
    }

    private static WindowStream OpenWindow(string file, long offset, FileMode mode,
                                           FileAccess access, int bufferSize, FileOptions options)
    {
        string? directory = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // The package is opened shared, because stage 3 holds its own write handle on it while our
        // read window feeds it.
        var fs = new FileStream(file, mode == FileMode.Create ? FileMode.OpenOrCreate : mode,
                                access == FileAccess.Read ? FileAccess.Read : FileAccess.ReadWrite,
                                FileShare.ReadWrite, bufferSize, options);
        try
        {
            // Create clears the WHOLE file, not just the window. Stock stage 3 opens the output
            // FileMode.Create, so the reserved head below the window starts as a hole and only the
            // finaliser's own bytes land there; in place nothing else ever truncates this file, so
            // skipping the head would leave an earlier package's bytes wherever the finaliser does
            // not write — a byte-parity divergence against a --spool build.
            if (mode == FileMode.Create && access != FileAccess.Read) fs.SetLength(0);
            if (fs.Length < offset && access != FileAccess.Read) fs.SetLength(offset);
        }
        catch
        {
            fs.Dispose();
            throw;
        }

        WindowStream window;
        try
        {
            window = new WindowStream(fs, offset, truncateOnOpen: mode == FileMode.Create);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
        if (access != FileAccess.Read) lastWrite = window;
        return window;
    }

    /// <summary>
    /// Site 24. The <c>FileMode</c> the outer-PFS writer opens its output with. In place the
    /// output already holds the plaintext inner image, so it must not be truncated; with the
    /// single-file build off this is the stock <c>Create</c>, which is what clears a stale package
    /// left by an earlier run.
    /// </summary>
    public static int OutputFileMode() => (int)(Enabled ? FileMode.OpenOrCreate : FileMode.Create);

    /// <summary>
    /// Site 24. The <c>FileShare</c> the outer-PFS writer opens its output with. In place the read
    /// window feeding it is a second handle on the same file.
    /// </summary>
    public static int OutputFileShare() => (int)(Enabled ? FileShare.ReadWrite : FileShare.None);

    /// <summary>Site 24. The declared length of a window, which no <c>FileInfo</c> can report.</summary>
    public static long LengthOf(string path)
    {
        if (!IsWindow(path)) return new FileInfo(path).Length;
        // After a relocation the image is shorter than the spool high-water mark by the slack the
        // layout left behind, so an explicit length wins when we have one.
        return relocatedLength ?? lastWrite?.HighWaterMark ?? 0;
    }

    /// <summary>
    /// Site 21. Replays the layout permutation inside the package instead of into a copy, and
    /// reports whether it handled the call at all — a false return lets the library's own body run,
    /// which is correct only for paths that are not ours.
    ///
    /// <para>The budget fallback is done here rather than by returning false, because the library's
    /// body finishes with <c>Move(tmp, path)</c> and <c>path</c> is the window string: that would
    /// leave a file named after the window and a package with no image.</para>
    /// </summary>
    public static bool MaterializeInPlace(string path, Func<Stream> open, long length)
    {
        if (!TryParse(path, out string file, out long offset)) return false;

        using (var target = new WindowStream(
            new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite,
                           1 << 20, FileOptions.RandomAccess), offset, false))
        {
            if (LayoutRelocator.TryMaterialize(target, open, length, MaxRelocationBytes, Log,
                                               out string? why))
            {
                relocatedLength = length;
                return true;
            }
            FallbackReason = why;
        }

        SpillToSpool(open, length);
        return true;
    }

    /// <summary>
    /// The fallback: materialise the relocated image into a real file and serve the window from
    /// there. This is the two-file build again, so it costs the disk the single-file path saves.
    /// </summary>
    private static void SpillToSpool(Func<Stream> open, long length)
    {
        Log?.Invoke("warning: " + FallbackReason + "; falling back to a spool copy for this " +
                    "build, which needs about twice the package size in free space.");

        Directory.CreateDirectory(FallbackDirectory);
        string spool = Path.Combine(FallbackDirectory,
                                    ".fpkg-inner-" + Guid.NewGuid().ToString("N") + ".spool");
        using (Stream source = open())
        using (var sink = new FileStream(spool, FileMode.CreateNew, FileAccess.Write,
                                         FileShare.None, 1 << 20, FileOptions.SequentialScan))
        {
            byte[] buffer = new byte[4 * 1024 * 1024];
            for (long done = 0; done < length;)
            {
                int n = (int)Math.Min(buffer.Length, length - done);
                source.ReadExactly(buffer.AsSpan(0, n));
                sink.Write(buffer, 0, n);
                done += n;
            }
        }
        fallbackSpool = spool;
        relocatedLength = length;
    }

    /// <summary>Cleanup guard: a window path must never reach <c>File.Delete</c>, because the file
    /// behind it is the package the build just produced. A fallback spool, on the other hand, is
    /// ours to remove.</summary>
    public static bool TryDeleteWindow(string path)
    {
        if (!IsWindow(path)) return false;
        if (fallbackSpool is not null)
        {
            try { File.Delete(fallbackSpool); } catch (IOException) { /* a leftover temp file */ }
            fallbackSpool = null;
        }
        return true;
    }

    private static bool TryParse(string? path, out string file, out long offset)
    {
        file = string.Empty;
        offset = 0;
        if (!IsWindow(path)) return false;

        string rest = path![Scheme.Length..];
        int at = rest.LastIndexOf('@');
        if (at <= 0 || !long.TryParse(rest[(at + 1)..], NumberStyles.None,
                                      CultureInfo.InvariantCulture, out offset))
            throw new InvalidDataException("Malformed in-place window path: " + path);

        file = rest[..at];
        return true;
    }
}

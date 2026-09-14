using System.Diagnostics;
using System.Security.Cryptography;
using Xunit;
using Xunit.Abstractions;

namespace FpkgVirtualSource.Tests;

/// <summary>
/// End-to-end acceptance test for building a PS5 package straight from a .ffpfsc/.exfat
/// container instead of a materialised folder (Task 13 of the ffpfsc-streaming-source plan).
///
/// <para>
/// <b>This test asserts that the two PKGs are byte-identical whole files — on this fixture, they
/// are.</b> That was not always believed: an earlier draft of this test and its documentation
/// claimed byte-identity was "unachievable by design", reasoning that
/// <see cref="FpkgVirtualSource.TreeOverlay"/> enumerates its virtual entries sorted by
/// (RelativePath, Ordinal) while the library's own folder walk uses
/// <c>DirectoryInfo.EnumerateFileSystemInfos()</c> (plain filesystem order), so AFID assignment
/// — and therefore physical layout and final SHA-256 — would necessarily differ. That reasoning
/// was never actually verified against a *correct* container build: the ~132 KB delta and
/// differing hash it was measured against (folder be40425d...b17eb1 vs. container
/// d640cb41...608d63e) turned out to be caused entirely by three files silently missing from the
/// container build (a real bug, fixed in <see cref="FpkgVirtualSource.TreeOverlay"/> — see its
/// history), not by an inherent ordering mismatch. With that bug (and one follow-on: an
/// over-eager fix pulling in a file the library's own folder walk excludes) fixed, this fixture's
/// two builds are verified byte-for-byte identical (`cmp` confirms), and the whole-package
/// assertion below pins that. <b>This is NOT a guarantee for every possible source tree</b> —
/// sorted order and filesystem-enumeration order are not specified to coincide in general, they
/// just do here — which is exactly why the per-file assertions below run FIRST and independently
/// of the whole-package one: if a future source tree ever does trigger a genuine layout
/// divergence, the failure should read as "every inner file matched, only physical layout
/// differs" (see the whole-package assertion's own failure message), not as an opaque hash
/// mismatch. Do not weaken any of these assertions on the theory that some future difference
/// must be "expected"; the last three times someone assumed that here, it was a real bug each
/// time.
/// </para>
///
/// <para>
/// What this test asserts, in the order it asserts them (diagnosis before blunt instrument):
///   1. the container build and the folder build contain the same set of inner files;
///   2. every one of those inner files is byte-identical between the two builds;
///   3. the container-built package passes `fpkg verify`;
///   4. the size delta is reported (via <see cref="ITestOutputHelper"/>) before any assertion
///      below could fail on it;
///   5. the two whole package files are byte-identical (same length, same SHA-256) — asserted
///      last, so a failure here always comes after 1-3 have already ruled out (or would have
///      caught) a content difference.
/// </para>
///
/// <para>
/// <b>/tmp/out2 currently carries two macOS filesystem artifacts (.DS_Store,
/// .fseventsd/) that were never fed into the .ffpfsc fixture.</b> Rather than comparing only
/// the intersection of the two file lists (which would hide a genuinely dropped file behind
/// "well, they just don't overlap there either"), this test stages a *copy* of /tmp/out2 with
/// exactly those two known entries removed, and diffs everything else exactly. A third-party
/// file appearing unexpectedly in that staged copy is not silently absorbed — see
/// <see cref="StageJunkFreeCopy"/>.
/// </para>
/// </summary>
public class AcceptanceTests : IDisposable
{
    private const string Fixture = "/tmp/out2.ffpfsc";
    private const string SourceTree = "/tmp/out2";

    /// <summary>
    /// Top-level entries of <see cref="SourceTree"/> known to be macOS filesystem noise that
    /// was never captured into <see cref="Fixture"/>. Anything else unrecognised is left in
    /// place deliberately, so a genuinely new/missing file surfaces as a real assertion
    /// failure below rather than being swallowed by an ever-growing ignore list.
    /// </summary>
    private static readonly string[] KnownJunkEntries = { ".DS_Store", ".fseventsd" };

    private readonly ITestOutputHelper _output;
    private readonly List<string> _cleanupDirs = new();

    public AcceptanceTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        foreach (var dir in _cleanupDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best effort; a leftover /tmp dir is not worth failing the test over */ }
        }
    }

    private string FreshTempDir(string tag)
    {
        // Hardcoded to /private/tmp rather than Path.GetTempPath(): on macOS both /tmp
        // (-> /private/tmp) and dotnet's own TMPDIR (under /var -> /private/var) resolve
        // through a symlink, and fpkg's `extract` command deliberately rejects an extraction
        // path that resolves through one (a zip-slip style guard) — see Program.cs's Extract().
        // /private/tmp itself is never a symlink. This project is macOS/arm64-only, so there is
        // no portability cost to hardcoding it.
        string dir = Path.Combine("/private/tmp", "fpkg-accept-" + tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _cleanupDirs.Add(dir);
        return dir;
    }

    /// <summary>
    /// Copies <see cref="SourceTree"/> into a fresh directory, excluding exactly
    /// <see cref="KnownJunkEntries"/>. Any *other* top-level entry is copied through unchanged
    /// — this is not a general-purpose junk filter, it removes only the two specific artifacts
    /// known not to be in the fixture, so nothing else can go missing from the comparison
    /// without the test noticing.
    /// </summary>
    private string StageJunkFreeCopy()
    {
        string dest = FreshTempDir("srcstage");
        foreach (var entry in Directory.GetFileSystemEntries(SourceTree))
        {
            string name = Path.GetFileName(entry);
            if (KnownJunkEntries.Contains(name)) continue;
            CopyRecursive(entry, Path.Combine(dest, name));
        }
        return dest;
    }

    private static void CopyRecursive(string src, string dst)
    {
        if (Directory.Exists(src))
        {
            Directory.CreateDirectory(dst);
            foreach (var child in Directory.GetFileSystemEntries(src))
                CopyRecursive(child, Path.Combine(dst, Path.GetFileName(child)));
        }
        else
        {
            File.Copy(src, dst);
        }
    }

    private static void RunFpkg(string tag, params string[] args)
    {
        string release = ReleaseFolder.Find();
        var psi = new ProcessStartInfo(Path.Combine(release, "fpkg"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = release,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0,
            $"fpkg {string.Join(' ', args)} failed for {tag} (exit {p.ExitCode}):\n{stdout}\n{stderr}");
    }

    private string Build(string source, string tag)
    {
        string outDir = FreshTempDir("out-" + tag);
        string tempDir = FreshTempDir("tmp-" + tag);
        RunFpkg(tag, "build", "--source", source, "--out", outDir, "--temp-dir", tempDir,
                "--kraken-backend", "BuiltIn", "--kraken-level", "6");
        return Directory.GetFiles(outDir, "*.pkg").Single();
    }

    /// <summary>Extracts a package's decoded inner PFS files via the CLI itself.</summary>
    private string ExtractInner(string pkg, string tag)
    {
        string dir = FreshTempDir("extract-" + tag);
        RunFpkg(tag, "extract", pkg, dir);
        string inner = Path.Combine(dir, "inner");
        Assert.True(Directory.Exists(inner), $"fpkg extract produced no inner/ for {tag} ({pkg})");
        return inner;
    }

    private static List<string> RelativeFilesSorted(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    private static string HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    [SkippableFact]
    public void ContainerAndFolderBuildsShareEveryInnerFileByteForByte()
    {
        Skip.IfNot(File.Exists(Fixture) && Directory.Exists(SourceTree),
                   $"needs {Fixture} and {SourceTree}");

        string folderSource = StageJunkFreeCopy();

        string folderPkg = Build(folderSource, "folder");
        string containerPkg = Build(Fixture, "container");

        long folderSize = new FileInfo(folderPkg).Length;
        long containerSize = new FileInfo(containerPkg).Length;
        string folderPkgHash = HashFile(folderPkg);
        string containerPkgHash = HashFile(containerPkg);
        _output.WriteLine($"folder package:    {folderSize:N0} bytes, sha256={folderPkgHash}");
        _output.WriteLine($"container package: {containerSize:N0} bytes, sha256={containerPkgHash}");
        _output.WriteLine(
            $"size delta: {Math.Abs(folderSize - containerSize):N0} bytes. Reported, not asserted " +
            "to be zero: TreeOverlay's container overlay enumerates sorted, the folder walk uses " +
            "filesystem order, and those two are not guaranteed to coincide on every source tree " +
            "(they do on this one — see the class doc comment). A non-zero delta here is not " +
            "itself a failure; the assertions below are what determine pass/fail.");

        // Criterion 3: the container-built package passes verification, checked explicitly and
        // independently of whatever the build's own (skippable, via --no-verify) internal check
        // happened to do.
        RunFpkg("container-verify", "verify", containerPkg);

        // Criteria 1 & 2: same set of inner files, every one byte-identical. Extraction (rather
        // than reasoning about the source trees directly) is deliberate: it exercises the exact
        // bytes a consumer of the PKG would see, catching a mistake anywhere in the build
        // pipeline, not just in how the source was read.
        string folderInner = ExtractInner(folderPkg, "folder");
        string containerInner = ExtractInner(containerPkg, "container");

        var folderFiles = RelativeFilesSorted(folderInner);
        var containerFiles = RelativeFilesSorted(containerInner);

        // Deliberately NOT Except()-then-ignore: both directions are checked and any difference
        // fails the test with the exact paths involved, so a dropped or newly-invented file
        // cannot hide as "well, it's not in the intersection either".
        var missingFromContainer = folderFiles.Except(containerFiles, StringComparer.Ordinal).ToList();
        var extraInContainer = containerFiles.Except(folderFiles, StringComparer.Ordinal).ToList();
        Assert.True(missingFromContainer.Count == 0 && extraInContainer.Count == 0,
            "inner file sets differ between the folder build and the container build.\n" +
            $"  missing from container build ({missingFromContainer.Count}): {string.Join(", ", missingFromContainer)}\n" +
            $"  extra in container build ({extraInContainer.Count}): {string.Join(", ", extraInContainer)}");
        Assert.NotEmpty(folderFiles);

        foreach (var rel in folderFiles)
        {
            string a = Path.Combine(folderInner, rel);
            string b = Path.Combine(containerInner, rel);
            long lenA = new FileInfo(a).Length;
            long lenB = new FileInfo(b).Length;
            Assert.True(lenA == lenB, $"size differs for '{rel}': folder={lenA} container={lenB}");

            string hashA = HashFile(a);
            string hashB = HashFile(b);
            Assert.True(hashA == hashB,
                $"content differs for '{rel}': folder sha256={hashA} container sha256={hashB}");
        }

        // Whole-package byte-identity, checked LAST and deliberately after every assertion
        // above: if this ever fails, the assertions above have already ruled out (or would have
        // caught) a missing/extra/differing inner file, so a failure here means the inner files
        // all matched but the *physical layout* of the two packages diverged -- i.e. the
        // difference is in AFID assignment / block ordering, not content. First thing to
        // suspect: TreeOverlay's container-overlay loop enumerates strictly
        // (RelativePath, Ordinal)-sorted, while the library's own folder walk uses
        // DirectoryInfo.EnumerateFileSystemInfos() (whatever order the filesystem returns) --
        // those two are not specified to coincide, they just do on this fixture today. See the
        // class doc comment before assuming a diverging hash here is "expected": the last three
        // times a difference here was assumed benign, it was a real bug (see NOTES.md's
        // "Acceptance criterion" for the full history).
        Assert.True(folderSize == containerSize,
            "folder and container packages are different sizes even though every inner file " +
            $"matched (folder={folderSize:N0} bytes, container={containerSize:N0} bytes). " +
            "The inner files are fine; suspect physical layout / AFID assignment order -- see " +
            "the class doc comment.");
        Assert.True(folderPkgHash == containerPkgHash,
            "folder and container packages are not byte-identical even though every inner file " +
            $"matched (folder sha256={folderPkgHash}, container sha256={containerPkgHash}). " +
            "The inner files are fine; this means the two packages' *physical layout* diverged, " +
            "not their content. First thing to suspect: TreeOverlay's container-overlay loop " +
            "enumerates sorted (RelativePath, Ordinal) while the library's own folder walk uses " +
            "DirectoryInfo.EnumerateFileSystemInfos() (filesystem order) -- those two orders are " +
            "not guaranteed to coincide, and a source tree where they don't would still pass " +
            "every assertion above while failing this one. See the class doc comment before " +
            "assuming that's fine to ignore.");
    }
}

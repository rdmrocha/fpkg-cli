using System.Security.Cryptography;
using Xunit;
using Xunit.Abstractions;

namespace Fpkg.Tests;

/// <summary>
/// The single-file build must be a pure disk-space optimisation: the same source must produce the
/// same package, byte for byte, whether the inner image was spooled to <c>--temp-dir</c> and
/// copied in, or written straight into the package and transformed in place.
///
/// <para><b>If this ever fails, the in-place path is wrong — it is not "expected to differ".</b>
/// The package's digest chain covers the inner image, so any divergence is a real content or
/// layout bug, and <c>--spool</c> is the oracle. Diagnose in this order, because it localises the
/// fault fastest: <c>fpkg inner-list &lt;pkg&gt; --sha256</c> on both (matching inner digests mean
/// the divergence is physical layout, i.e. the relocation pass replayed the permutation
/// differently from PhysicalLayoutStream); then <c>cmp -l</c> for the first differing offset —
/// below 0x10000 is the FIH/CNT, and 0x10000 + n*65536 is outer data block n, which maps back to
/// an inner-image offset.</para>
///
/// <para>Every build gets its own <c>--temp-dir</c>: concurrent builds sharing one interfere
/// through the CLI's abandoned-staging recovery, which is pre-existing behaviour unrelated to
/// this feature.</para>
/// </summary>
public class InPlaceParityTests(ITestOutputHelper output)
{
    /// <summary>
    /// Compressible, incompressible and duplicated data in a nested tree, so the layout planner
    /// has gaps to coalesce and extents to relocate rather than a degenerate identity plan.
    /// </summary>
    private static string BuildSource(int scale = 1)
    {
        string root = Path.Combine(Path.GetTempPath(), "ip-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sce_sys"));
        File.WriteAllText(Path.Combine(root, "sce_sys", "param.json"), SyntheticPackage.ParamJson);

        var rng = new Random(1234);
        byte[] block = new byte[1 << 20];
        rng.NextBytes(block);

        foreach ((string rel, int size, bool duplicated) in new (string, int, bool)[]
                 {
                     ("data/a.bin", 12 << 20, false),
                     ("data/b.bin", 8 << 20, true),
                     ("data/deep/nested/c.bin", 6 << 20, true),
                     ("data/text.txt", 4 << 20, false),
                 })
        {
            string path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var file = File.Create(path);
            for (int written = 0; written < size * scale; written += block.Length)
            {
                if (!duplicated) rng.NextBytes(block);
                file.Write(block, 0, Math.Min(block.Length, size * scale - written));
            }
        }
        return root;
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string Build(string source, string tag, params string[] extra) =>
        SyntheticPackage.BuildAt(source, tag,
            [.. extra, "--temp-dir", FreshTemp(tag)]);

    private static string FreshTemp(string tag)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ip-temp-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void InPlaceAndSpoolBuildsAreByteIdentical()
    {
        string source = BuildSource();
        string inPlace = Build(source, "inplace");
        string spooled = Build(source, "spool", "--spool");

        long inPlaceSize = new FileInfo(inPlace).Length;
        long spooledSize = new FileInfo(spooled).Length;
        output.WriteLine($"in-place: {inPlaceSize:N0} bytes, {Sha256(inPlace)}");
        output.WriteLine($"spool:    {spooledSize:N0} bytes, {Sha256(spooled)}");

        Assert.Equal(spooledSize, inPlaceSize);
        Assert.Equal(Sha256(spooled), Sha256(inPlace));
    }

    [Fact]
    public void AnIdentityLayoutStillBuildsInPlace()
    {
        // One file of incompressible data gives the planner nothing to move, so
        // MaterializePhysicalLayout is never called and the image must already be final in place.
        string root = Path.Combine(Path.GetTempPath(), "ip-flat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sce_sys"));
        File.WriteAllText(Path.Combine(root, "sce_sys", "param.json"), SyntheticPackage.ParamJson);
        byte[] data = new byte[8 << 20];
        new Random(7).NextBytes(data);
        File.WriteAllBytes(Path.Combine(root, "only.bin"), data);

        Assert.Equal(Sha256(Build(root, "flat-spool", "--spool")),
                     Sha256(Build(root, "flat-inplace")));
    }

    /// <summary>
    /// The relocating layout. On a default build the optimiser never selects a moving plan — the
    /// CLI keeps --playgo-fix on, which vetoes coalescing and alignment — so the in-place
    /// relocation pass would go entirely unexercised. --force-coalescing overrides that veto (it
    /// makes a worse package, which is irrelevant here: what is under test is that both build
    /// paths produce the SAME package). The test asserts the relocation actually ran, so it
    /// cannot quietly go vacuous if the optimiser's thresholds shift.
    /// </summary>
    [Fact]
    public void ARelocatingLayoutIsMaterialisedInPlaceAndStillMatches()
    {
        string source = BuildSource();
        string temp = FreshTemp("relocate");
        var (exit, log) = SyntheticPackage.RunFpkg(
            ["build", "--source", source, "--out", FreshTemp("relocate-out"),
             "--temp-dir", temp, "--force-coalescing"]);
        output.WriteLine(log);
        Assert.Equal(0, exit);
        Assert.Contains("materialised in place", log);

        string inPlace = WrittenPackage(log);
        string spooled = Build(source, "relocate-spool", "--force-coalescing", "--spool");

        output.WriteLine($"in-place: {Sha256(inPlace)}");
        output.WriteLine($"spool:    {Sha256(spooled)}");
        Assert.Equal(Sha256(spooled), Sha256(inPlace));
    }

    /// <summary>
    /// A staging budget of one byte forces the relocation to give up. The build must still
    /// succeed, say so, and produce the oracle's bytes — a space optimisation that cannot fit is
    /// never allowed to become a failed or a different build.
    /// </summary>
    [Fact]
    public void ABudgetTooSmallToRelocateFallsBackAndStillMatches()
    {
        string source = BuildSource();
        string oracle = Build(source, "budget-oracle", "--force-coalescing", "--spool");

        var (exit, log) = SyntheticPackage.RunFpkg(
            ["build", "--source", source, "--out", FreshTemp("budget-out"),
             "--temp-dir", FreshTemp("budget-temp"),
             "--force-coalescing", "--relocation-budget", "1"]);
        output.WriteLine(log);

        Assert.Equal(0, exit);
        Assert.Contains("fell back to a spool", log);
        string fellBack = WrittenPackage(log);
        Assert.Equal(Sha256(oracle), Sha256(fellBack));
    }

    /// <summary>The path the CLI prints on its "wrote ..." line.</summary>
    private static string WrittenPackage(string log)
    {
        foreach (string line in log.Split('\n'))
            if (line.StartsWith("wrote ", StringComparison.Ordinal))
                return line["wrote ".Length..].Trim();
        throw new InvalidOperationException("the build did not report a written package:\n" + log);
    }

    [Fact]
    public void AnInPlaceBuildLeavesNoFullSizeFileInTheTempDir()
    {
        string source = BuildSource();
        string temp = FreshTemp("peak");

        long peak = 0;
        using var watcher = new Timer(_ =>
        {
            long now = 0;
            try
            {
                foreach (string f in Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories))
                {
                    try { now += new FileInfo(f).Length; } catch (IOException) { /* vanished */ }
                }
            }
            catch (DirectoryNotFoundException) { /* not created yet */ }
            long seen = Interlocked.Read(ref peak);
            if (now > seen) Interlocked.Exchange(ref peak, now);
        }, null, 0, 25);

        string pkg = SyntheticPackage.BuildAt(source, "peak", ["--temp-dir", temp]);
        watcher.Change(Timeout.Infinite, Timeout.Infinite);

        long package = new FileInfo(pkg).Length;
        output.WriteLine($"package {package:N0} bytes, peak temp {peak:N0} bytes " +
                         $"({100.0 * peak / package:F1}% of the package)");

        // The inner image is the bulk of the package. If it were still being spooled, peak temp
        // would be on the order of the package itself; half is a generous ceiling that still
        // fails loudly if the spool comes back.
        Assert.True(peak < package / 2,
            $"peak temp {peak:N0} is not clearly below half the package ({package:N0}); " +
            "the inner image is probably still being spooled");
    }

    [Fact]
    public void AnInPlaceBuildPassesTheLibrarysOwnFullVerifier()
    {
        string pkg = Build(BuildSource(), "verify");
        var (exit, log) = SyntheticPackage.RunFpkg(["verify", pkg, "--full"]);
        output.WriteLine(log);
        Assert.Equal(0, exit);
    }
}

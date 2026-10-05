using System.Security.Cryptography;
using Fpkg.Cli;
using Fpkg.Cli.PlayGo;
using Xunit;

namespace Fpkg.Tests;

/// <summary>
/// LibProsperoPkg 0.6.11 keeps a source's own PlayGo map and rebuilds only its extents. When it
/// will, fpkg's own repair must step aside rather than redo the library's work, and the pieces the
/// library does not do -- distinct language fillers, Sony's scenario document, the SI records --
/// are finished on top of the library's tables.
///
/// <para>The oracle is the round trip. <see cref="SyntheticPackage"/> is built by fpkg's repair;
/// extracting it gives a source whose PlayGo tables map every filler to its language chunk, which
/// is exactly the shape a dump of a Sony-built package has. Rebuilding that source through the
/// library's source-map mode must reproduce the original package byte for byte.</para>
/// </summary>
public class PlayGoSourceMapTests
{
    /// <summary>
    /// The temp directory with its symlinks resolved. On macOS it lives under /var, a link to
    /// /private/var, and the library's extractor refuses any output path that passes through a link.
    /// </summary>
    private static string Temp { get; } = Resolve(Path.GetTempPath());

    private static string Resolve(string path)
    {
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (full.Length == 0) return Path.DirectorySeparatorChar.ToString();
        string? parent = Path.GetDirectoryName(full);
        string resolved = parent is null ? full : Path.Combine(Resolve(parent), Path.GetFileName(full));
        var info = new DirectoryInfo(resolved);
        return info.LinkTarget is null ? resolved : info.ResolveLinkTarget(true)!.FullName;
    }

    private static readonly Lazy<string> Extracted = new(Extract, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The synthetic package unpacked into a rebuildable tree: inner files plus sce_sys.</summary>
    private static string Extract()
    {
        string root = Path.Combine(Temp, "pg-map-" + Guid.NewGuid().ToString("N"));
        var (exit, log) = SyntheticPackage.RunFpkg(["extract", SyntheticPackage.Path, root]);
        Assert.True(exit == 0, log);
        (exit, log) = SyntheticPackage.RunFpkg(["extract", SyntheticPackage.Path, root, "--rebuild-source"]);
        Assert.True(exit == 0, log);
        string tree = Path.Combine(root, "inner");
        foreach (string file in Directory.GetFiles(Path.Combine(root, "source", "sce_sys")))
            File.Copy(file, Path.Combine(tree, "sce_sys", Path.GetFileName(file)), overwrite: true);
        return tree;
    }

    /// <summary>A private copy, so a test that changes the tree cannot disturb another.</summary>
    private static string CopyOfExtracted()
    {
        string copy = Path.Combine(Temp, "pg-map-copy-" + Guid.NewGuid().ToString("N"));
        foreach (string file in Directory.GetFiles(Extracted.Value, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(copy, Path.GetRelativePath(Extracted.Value, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return copy;
    }

    private static (string Package, string Log) Build(string source, params string[] extra)
    {
        string output = Path.Combine(Temp, "pg-map-out-" + Guid.NewGuid().ToString("N"));
        string temp = Path.Combine(Temp, "pg-map-temp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var (exit, log) = SyntheticPackage.RunFpkg(
            ["build", "--source", source, "--out", output, "--temp-dir", temp, .. extra]);
        Assert.True(exit == 0, log);
        return (Directory.GetFiles(output, "*.pkg").Single(), log);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    [Fact]
    public void ALibrarySourceMapRebuildReproducesTheRepairBuildByteForByte()
    {
        var (package, log) = Build(Extracted.Value);

        Assert.Contains("the library keeps the source's own map", log);
        Assert.Contains("kept the source's 31 language filler(s)", log);
        Assert.Contains("Completing the library's source-map PlayGo metadata", log);
        Assert.DoesNotContain("Deriving PlayGo metadata from the measured layout", log);
        Assert.DoesNotContain("sce_sys/playgo-chunk.dat", log.Split('\n')
            .Where(l => l.Contains("set aside")).DefaultIfEmpty("").Aggregate((a, b) => a + b));
        Assert.Equal(Sha256(SyntheticPackage.Path), Sha256(package));
    }

    [Fact]
    public void ForcingTheRepairReusesTheSourceFillersAndLeavesThemInPlace()
    {
        string source = CopyOfExtracted();
        var (package, log) = Build(source, "--force-playgo-fix");

        Assert.Contains("--force-playgo-fix: the source map", log);
        Assert.Contains("reusing the source's own 31 language filler(s)", log);
        Assert.Contains("Deriving PlayGo metadata from the measured layout", log);
        Assert.Equal(31, Directory.GetFiles(Path.Combine(source, "playgo-languages")).Length);
        Assert.Equal(Sha256(SyntheticPackage.Path), Sha256(package));
    }

    [Fact]
    public void ALanguageSubsetOverridesTheSourceMapSoTheRepairApplies()
    {
        string source = CopyOfExtracted();
        var (_, log) = Build(source, "--playgo-languages", "en-US,fr-FR");

        Assert.DoesNotContain("the library keeps the source's own map", log);
        Assert.Contains("sce_sys/playgo-chunk.dat", log);
        Assert.Contains("Deriving PlayGo metadata from the measured layout", log);
        Assert.Equal(31, Directory.GetFiles(Path.Combine(source, "playgo-languages")).Length);
    }

    [Fact]
    public void NoPlayGoFixLeavesTheLibrarysSourceMapUntouched()
    {
        var (_, log) = Build(CopyOfExtracted(), "--no-playgo-fix");

        Assert.Contains("the library keeps the source's own map", log);
        Assert.DoesNotContain("kept the source's 31 language filler(s)", log);
        Assert.DoesNotContain("Completing the library's source-map PlayGo metadata", log);
        Assert.DoesNotContain("Deriving PlayGo metadata", log);
    }

    [Theory]
    [InlineData("sce_sys/playgo-chunk.dat", true)]
    [InlineData("sce_sys/PLAYGO-FICM.DAT", true)]
    [InlineData("sce_sys/playgo-scenario.json", true)]
    [InlineData("sce_sys/playgo-manifest.xml", false)]
    [InlineData("sce_sys/app/playgo-chunk.dat", false)]
    [InlineData("playgo-chunk.dat", false)]
    public void OnlyTheLibrarysPlayGoInputsAreKeptOutOfTheSweep(string relative, bool kept) =>
        Assert.Equal(kept, Program.SceSysQuarantine.IsKept(
            relative.Replace('/', Path.DirectorySeparatorChar), Program.SceSysQuarantine.PlayGoInputs));

    [Fact]
    public void TheNapsMetaRecordsAreOnlyRebuiltForTheMeasuredShape()
    {
        var plgx = Plgx.Parse(ReadChunkDat());
        Assert.NotNull(PlayGoRebuild.FromChunkDat(plgx.Build(), out _));

        // Let chunk 1 also claim chunk 2's extent: a shared extent is a shape the naps_meta format
        // was never measured on, so the library's records must be left alone.
        uint stolen = plgx.Chunks[2].ExtentRefs[0];
        plgx.Chunks[1].ExtentRefs.Add(stolen);
        Assert.Null(PlayGoRebuild.FromChunkDat(plgx.Build(), out string? why));
        Assert.Contains("shared", why);
    }

    private static byte[] ReadChunkDat()
    {
        string root = Path.Combine(Temp, "pg-map-cnt-" + Guid.NewGuid().ToString("N"));
        var (exit, log) = SyntheticPackage.RunFpkg(["extract", SyntheticPackage.Path, root, "--rebuild-source"]);
        Assert.True(exit == 0, log);
        return File.ReadAllBytes(Path.Combine(root, "source", "sce_sys", "playgo-chunk.dat"));
    }
}

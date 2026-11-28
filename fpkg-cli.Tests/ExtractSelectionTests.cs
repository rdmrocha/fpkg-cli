using Fpkg.Cli;
using Xunit;

namespace Fpkg.Tests;

/// <summary>
/// <c>fpkg extract &lt;pkg&gt; &lt;dir&gt; &lt;path&gt;...</c>: the named files and folders,
/// wherever they live, in one game tree. The point is that the caller does not have to know that
/// <c>Media/</c> is in the inner PFS and <c>sce_sys/param.json</c> is a CNT entry.
/// </summary>
public class ExtractSelectionTests
{
    [Theory]
    [InlineData("eboot.bin", "eboot.bin")]
    [InlineData("/sce_module/", "sce_module")]
    [InlineData("./sce_sys", "sce_sys")]
    [InlineData(@"sce_sys\about", "sce_sys/about")]
    [InlineData("uroot/fakelib", "fakelib")]
    [InlineData("a//b/./c", "a/b/c")]
    public void TypedPathsAreBroughtToTheLibrarysForm(string typed, string expected) =>
        Assert.Equal(expected, ExtractSelection.Normalize(typed));

    [Theory]
    [InlineData(".")]
    [InlineData("/")]
    [InlineData("uroot")]
    public void APathThatMeansEverythingIsRefused(string typed) =>
        Assert.Throws<ArgumentException>(() => new ExtractSelection([typed]));

    [Fact]
    public void APathThatClimbsOutIsRefused() =>
        Assert.Throws<ArgumentException>(() => new ExtractSelection(["sce_sys/../../etc"]));

    [Theory]
    [InlineData("eboot.bin", true)]
    [InlineData("EBOOT.BIN", true)]
    [InlineData("sce_module/libc.prx", true)]
    [InlineData("sce_module/deep/x.prx", true)]
    [InlineData("sce_sys/param.json", true)]
    [InlineData("sce_modules/libc.prx", false)]  // whole segments only
    [InlineData("eboot.bin.bak", false)]
    [InlineData("data/eboot.bin", false)]        // anchored at the root
    public void ASelectorTakesItselfAndEverythingUnderIt(string path, bool expected) =>
        Assert.Equal(expected, new ExtractSelection(["eboot.bin", "sce_module", "sce_sys"]).Accepts(path));

    [Fact]
    public void UnmatchedNamesOnlyTheSelectorsNothingSatisfies() =>
        Assert.Equal(["fakelib"],
                     new ExtractSelection(["eboot.bin", "fakelib"]).Unmatched(["eboot.bin", "sce_sys/param.json"]));

    private static string FreshOut() =>
        Path.Combine(SyntheticPackage.ResolvedTemp, "extract-sel-" + Guid.NewGuid().ToString("N"));

    private static string[] Tree(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                 .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
                 .Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void InnerAndCntPathsLandInOneTree()
    {
        string root = FreshOut();
        var (exit, log) = SyntheticPackage.RunFpkg(
            ["extract", SyntheticPackage.Path, root, "Media/Sub", "data.bin", "sce_sys/param.json"]);
        Assert.True(exit == 0, log);

        Assert.Equal(["Media/Sub/nested.dat", "data.bin", "sce_sys/param.json"], Tree(root));
    }

    [Fact]
    public void AFolderSelectorTakesTheWholeSubtree()
    {
        string root = FreshOut();
        var (exit, log) = SyntheticPackage.RunFpkg(["extract", SyntheticPackage.Path, root, "Media"]);
        Assert.True(exit == 0, log);

        Assert.Equal(["Media/Sub/nested.dat", "Media/big.dat", "Media/small.dat"], Tree(root));
    }

    /// <summary>The CNT copy must win where both exist, as ListExtractionFiles documents.</summary>
    [Fact]
    public void ASelectedSceSysFileIsTheCntCopy()
    {
        string root = FreshOut();
        var (exit, log) = SyntheticPackage.RunFpkg(["extract", SyntheticPackage.Path, root, "sce_sys"]);
        Assert.True(exit == 0, log);

        string cnt = FreshOut();
        (exit, log) = SyntheticPackage.RunFpkg(["extract", SyntheticPackage.Path, cnt, "--rebuild-source"]);
        Assert.True(exit == 0, log);
        foreach (string file in Directory.GetFiles(Path.Combine(cnt, "source", "sce_sys")))
            Assert.Equal(File.ReadAllBytes(file),
                         File.ReadAllBytes(Path.Combine(root, "sce_sys", Path.GetFileName(file))));
    }

    [Fact]
    public void ATypoFailsBeforeAnythingIsWritten()
    {
        string root = FreshOut();
        var (exit, log) = SyntheticPackage.RunFpkg(["extract", SyntheticPackage.Path, root, "data.bin", "eboot.bin"]);

        Assert.NotEqual(0, exit);
        Assert.Contains("eboot.bin", log);
        Assert.Empty(Tree(root));
    }

    [Fact]
    public void FlagsStillParseAfterThePaths()
    {
        string root = FreshOut();
        var (exit, log) = SyntheticPackage.RunFpkg(
            ["extract", SyntheticPackage.Path, root, "data.bin", "--passcode", SyntheticPackage.Passcode]);
        Assert.True(exit == 0, log);

        Assert.Equal(["data.bin"], Tree(root));
    }

    [Theory]
    [InlineData("eboot.bin")]
    [InlineData("sce_sys/param.json")]
    [InlineData("deep/nested/anything.dat")]
    public void EverythingAcceptsAnyPath(string path) =>
        Assert.True(ExtractSelection.Everything.Accepts(path));

    /// <summary>
    /// --all is the union of a no-path extract (inner/) and --rebuild-source (source/), in one
    /// tree, with the CNT copy on top where both have a path.
    /// </summary>
    [Fact]
    public void AllIsTheInnerTreeWithTheRebuildSourceOverIt()
    {
        string root = FreshOut();
        var (exit, log) = SyntheticPackage.RunFpkg(["extract", SyntheticPackage.Path, root, "--all"]);
        Assert.True(exit == 0, log);

        string split = FreshOut();
        (exit, log) = SyntheticPackage.RunFpkg(["extract", SyntheticPackage.Path, split]);
        Assert.True(exit == 0, log);
        (exit, log) = SyntheticPackage.RunFpkg(["extract", SyntheticPackage.Path, split, "--rebuild-source"]);
        Assert.True(exit == 0, log);

        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string f in Tree(Path.Combine(split, "inner"))) expected[f] = Path.Combine(split, "inner", f);
        foreach (string f in Tree(Path.Combine(split, "source"))) expected[f] = Path.Combine(split, "source", f);

        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), Tree(root));
        foreach (var (rel, source) in expected)
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(root, rel)));
    }

    [Fact]
    public void AllWithPathsIsRefusedBeforeAnythingIsWritten()
    {
        string root = FreshOut();
        var (exit, log) = SyntheticPackage.RunFpkg(["extract", SyntheticPackage.Path, root, "data.bin", "--all"]);

        Assert.NotEqual(0, exit);
        Assert.Contains("--all", log);
        Assert.False(Directory.Exists(root) && Tree(root).Length > 0);
    }

    /// <summary>Each file is named on stdout as it is written, with the overall percent.</summary>
    [Fact]
    public void EveryExtractedFileIsReportedAsItIsWritten()
    {
        string root = FreshOut();
        var (exit, log) = SyntheticPackage.RunFpkg(["extract", SyntheticPackage.Path, root, "--all"]);
        Assert.True(exit == 0, log);

        var lines = log.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        foreach (string file in Tree(root))
            Assert.Contains(lines, l => System.Text.RegularExpressions.Regex.IsMatch(
                l, @"^  \[\s*\d{1,3}%\] " + System.Text.RegularExpressions.Regex.Escape(file) + "$"));
        Assert.Contains("  [100%] inner PFS", lines);
    }
}

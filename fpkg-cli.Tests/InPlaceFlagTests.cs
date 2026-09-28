using Xunit;
using Xunit.Abstractions;

namespace Fpkg.Tests;

/// <summary>
/// The CLI end of the single-file build: it is the default, <c>--spool</c> opts out, and the
/// build says which one it used — a build log that does not name the path it took is a build
/// nobody can diagnose after the fact.
/// </summary>
public class InPlaceFlagTests(ITestOutputHelper output)
{
    private static string TinySource()
    {
        string root = Path.Combine(Path.GetTempPath(), "ip-tiny-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sce_sys"));
        File.WriteAllText(Path.Combine(root, "sce_sys", "param.json"), SyntheticPackage.ParamJson);
        var data = new byte[1 << 20];
        new Random(3).NextBytes(data);
        File.WriteAllBytes(Path.Combine(root, "data.bin"), data);
        return root;
    }

    [Fact]
    public void SpoolFlagIsDocumented()
    {
        var (exit, log) = SyntheticPackage.RunFpkg(["help"]);
        Assert.Equal(0, exit);
        Assert.Contains("--spool", log);
    }

    [Fact]
    public void TheDefaultBuildWritesTheInnerImageIntoThePackage()
    {
        string source = TinySource();
        var (exit, log) = SyntheticPackage.RunFpkg(
            ["build", "--source", source, "--out", FreshOut("default"),
             "--temp-dir", FreshOut("default-temp")]);
        output.WriteLine(log);

        Assert.Equal(0, exit);
        Assert.Contains("Single-file build", log);
    }

    [Fact]
    public void TheSpoolFlagRestoresTheTwoFileBuild()
    {
        string source = TinySource();
        var (exit, log) = SyntheticPackage.RunFpkg(
            ["build", "--source", source, "--out", FreshOut("spool"),
             "--temp-dir", FreshOut("spool-temp"), "--spool"]);
        output.WriteLine(log);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("Single-file build", log);
    }

    /// <summary>
    /// Every build here gets its own --temp-dir. Concurrent builds sharing one temp directory
    /// interfere through the abandoned-staging recovery the CLI runs at startup, which scans that
    /// directory for another build's records; the symptom is a PlayGo filler vanishing mid-build
    /// ("no extent was derived for language chunk 1"). That is pre-existing CLI behaviour, not
    /// something the single-file build introduced, but these tests are what made it reachable
    /// from the suite.
    /// </summary>
    private static string FreshOut(string tag)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ip-flag-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}

using LibProsperoPkg.PKG;
using Xunit;

public class TestPackageTests
{
    [SkippableFact]
    public void LocatesTheTestPackage()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        Assert.EndsWith(".pkg", TestPackage.Path);
        // TestPackage.Path is a symlink in this worktree, and FileInfo.Length reports the
        // link's own (lstat) size rather than the target's — File.OpenRead follows the link,
        // so its Stream.Length gives the real size.
        using var stream = File.OpenRead(TestPackage.Path);
        Assert.Equal(661_006_510, stream.Length);
    }

    [Fact]
    public void FindsTheReleaseFolder() =>
        Assert.True(File.Exists(Path.Combine(TestPackage.RepoRoot, "LibProsperoPkg.dll")));

    [SkippableFact]
    public void ReadsTheContentId()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        Assert.Equal(TestPackage.ContentId, ProsperoPkgReader.Read(TestPackage.Path).Header!.ContentId);
    }
}

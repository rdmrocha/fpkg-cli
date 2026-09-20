using Xunit;

/// <summary>
/// The source-exclusion rules, transcribed from Sony's folder-to-GP5 generator. The scopes are the
/// whole point: the same name is excluded at one depth and kept at another, and getting that wrong
/// either leaves extra files in the package or silently drops real game content.
/// </summary>
public class SourceExclusionTests
{
    private static bool Excluded(string relative) =>
        Fpkg.Cli.Program.SceSysQuarantine.Excluded(relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>
    /// PROJECT_SUFFIXES is tree-wide and fires before any scoping rule. These two are the files a
    /// sce_sys-scoped sweep provably could not reach, and were the extra inner files against the
    /// the large title oracle.
    /// </summary>
    [Theory]
    [InlineData("eboot.bin.esbak")]
    [InlineData("sce_module/libc.prx.esbak")]
    [InlineData("sce_sys/about/right.sprx.esbak")]
    [InlineData("build/ps5/main/deep/thing.ESBAK")]
    [InlineData("project.gp4")]
    [InlineData("nested/dir/project.gp5")]
    public void ProjectSuffixesAreExcludedAtEveryDepthAndCaseInsensitively(string path) =>
        Assert.True(Excluded(path));

    /// <summary>Only the LAST extension counts, so a name that merely contains one is kept.</summary>
    [Theory]
    [InlineData("eboot.esbak.bin")]
    [InlineData("gp5notes.txt")]
    [InlineData("data/esbak")]
    public void AnExtensionThatIsNotLastDoesNotCount(string path) => Assert.False(Excluded(path));

    /// <summary>GENERATED_SCE_SYS_FILES applies to DIRECT children of sce_sys only.</summary>
    [Theory]
    [InlineData("sce_sys/playgo-chunk.dat")]
    [InlineData("sce_sys/license.dat")]
    [InlineData("sce_sys/param.sfo")]
    [InlineData("sce_sys/target-deltainfo.dat")]
    public void GeneratedSceSysFilesAreExcludedAtDepthTwo(string path) => Assert.True(Excluded(path));

    [Theory]
    [InlineData("sce_sys/nested/playgo-chunk.dat")]  // deeper than Sony's len(parts) == 2
    [InlineData("playgo-chunk.dat")]                 // outside sce_sys entirely
    [InlineData("game/license.dat")]
    public void TheSameNamesSurviveOutsideThatOneDirectoryLevel(string path) => Assert.False(Excluded(path));

    /// <summary>The about/ subtree goes at any depth under sce_sys.</summary>
    [Theory]
    [InlineData("sce_sys/about/right.sprx")]
    [InlineData("sce_sys/deep/about/thing.bin")]
    public void TheAboutSubtreeIsExcluded(string path) => Assert.True(Excluded(path));

    [Fact]
    public void AnAboutDirectoryOutsideSceSysIsKept() => Assert.False(Excluded("game/about/readme.bin"));

    /// <summary>Root-level directories only: deeper ones are real game content.</summary>
    [Theory]
    [InlineData("sce_suppl/x.bin", true)]
    [InlineData("sce_sc/x.bin", true)]
    [InlineData(".gp5-assets/x.bin", true)]
    [InlineData("game/sce_suppl/x.bin", false)]
    [InlineData("data/sce_sc/level.pak", false)]
    public void RootDirectoriesAreRootOnly(string path, bool excluded) =>
        Assert.Equal(excluded, Excluded(path));

    [Theory]
    [InlineData("ampr_emu.index", true)]
    [InlineData("data/ampr_emu.index", false)]
    [InlineData("fakelib/libsceampr.sprx", true)]
    [InlineData("fakelib/libsceplaygo.sprx", true)]
    [InlineData("sce_module/libsceampr.sprx", false)]
    public void ExactPathRulesMatchThePathAndNotJustTheName(string path, bool excluded) =>
        Assert.Equal(excluded, Excluded(path));

    [Fact]
    public void TheTreeWideScenarioNameSuffixIsExcluded() =>
        Assert.True(Excluded("anywhere/the reference fixture.playgo-scenario.json"));

    /// <summary>
    /// The two deliberate departures from Sony's list. keystone and pfs-version.dat are in
    /// GENERATED_SCE_SYS_FILES but our builder requires the source keystone and BOTH appear as
    /// inner files in the oracle; the source .dds are kept because they already match the oracle's
    /// CNT entries and re-encoding would risk content that currently agrees.
    /// </summary>
    [Theory]
    [InlineData("sce_sys/keystone")]
    [InlineData("sce_sys/pfs-version.dat")]
    [InlineData("sce_sys/icon0.dds")]
    [InlineData("sce_sys/pic0.dds")]
    public void TheDeliberateDeparturesAreKept(string path) => Assert.False(Excluded(path));

    [Theory]
    [InlineData("eboot.bin")]
    [InlineData("sce_module/libc.prx")]
    [InlineData("sce_sys/param.json")]
    [InlineData("sce_sys/icon0_07.png")]
    [InlineData("Media/Modules/PS5Util.prx")]
    public void OrdinaryContentIsNeverExcluded(string path) => Assert.False(Excluded(path));
}

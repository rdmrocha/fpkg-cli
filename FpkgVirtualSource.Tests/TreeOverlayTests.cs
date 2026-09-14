using System.Text;
using LibProsperoPkg.PFS;
using Xunit;

namespace FpkgVirtualSource.Tests;

/// <summary>
/// <see cref="TreeOverlay.Populate"/> is called from patched IL, so a wrong reflected member
/// name or a mis-shaped tree would otherwise stay invisible until the full builder runs (the
/// very last task of the project). These tests exercise the real reflection against the real
/// LibProsperoPkg types, not a hand-rolled stand-in for FSDir/FSFile.
/// </summary>
public class TreeOverlayTests
{
    private static byte[] Json => Encoding.ASCII.GetBytes("{}");

    [Fact]
    public void IgnoresOrdinaryDirectories()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try { Assert.False(TreeOverlay.Populate(new object(), dir)); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// Builds a container with an app root nested one level below the volume root
    /// ("MyGame"), a sub-directory under it ("Media"), and a sce_sys/param.json that must
    /// NOT show up in the overlaid tree -- the caller's own filesystem walk already added it
    /// from the staged copy on disk. Verifies:
    ///  - files and directories land under the right parent, by real reflection against the
    ///    genuine FSDir/FSFile types (not a stand-in), so a wrong field name fails loudly here
    ///    rather than surfacing only once the full builder runs;
    ///  - Parent back-references are wired correctly at every level;
    ///  - sce_sys is not duplicated from the container;
    ///  - names are relative to the app root, not the volume root -- if the overlay used the
    ///    volume root by mistake, a "MyGame" directory would appear in node.Dirs instead of
    ///    eboot.bin/Media landing directly on it.
    /// </summary>
    [Fact]
    public void PopulatesTheTreeFromTheContainerRelativeToTheAppRoot()
    {
        var volume = new TestVolume()
            .Add("MyGame/sce_sys/param.json", Json)
            .Add("MyGame/eboot.bin", [0x7F, (byte)'E', (byte)'L', (byte)'F'])
            .Add("MyGame/Media/x.bin", [1, 2, 3]);

        string handle = ContainerRegistry.Register(TestContainer.Open(volume));
        string root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, TreeOverlay.MarkerFileName), handle);

            var node = new FSDir();
            bool handled = TreeOverlay.Populate(node, root);

            Assert.True(handled);

            // Relative to the app root: no "MyGame" directory, and no "sce_sys" anywhere.
            Assert.DoesNotContain(node.Dirs, d => d.name == "MyGame");
            Assert.DoesNotContain(node.Dirs, d => d.name == "sce_sys");
            Assert.DoesNotContain(node.Files, f => f.name == "sce_sys");

            var eboot = Assert.Single(node.Files);
            Assert.Equal("eboot.bin", eboot.name);
            Assert.Same(node, eboot.Parent);

            var media = Assert.Single(node.Dirs);
            Assert.Equal("Media", media.name);
            Assert.Same(node, media.Parent);
            Assert.Empty(media.Dirs);

            var xbin = Assert.Single(media.Files);
            Assert.Equal("x.bin", xbin.name);
            Assert.Same(media, xbin.Parent);
        }
        finally
        {
            ContainerRegistry.Release(handle);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// When sce_sys/param.json sits at the volume root, <see cref="Container.FindAppRoot"/>
    /// returns "" and <see cref="TreeOverlay.Populate"/>'s prefix is empty -- the branch that
    /// skips filtering by prefix entirely. This is the realistic case for this project's own
    /// fixture (MkPFS packs /tmp/out2 directly, so sce_sys sits at the volume root), so it is
    /// worth pinning here rather than leaving it to the first full end-to-end build to exercise.
    /// </summary>
    [Fact]
    public void PopulatesTheTreeWhenTheAppRootIsTheVolumeRoot()
    {
        var volume = new TestVolume()
            .Add("sce_sys/param.json", Json)
            .Add("eboot.bin", [0x7F, (byte)'E', (byte)'L', (byte)'F'])
            .Add("Media/x.bin", [1, 2, 3]);

        string handle = ContainerRegistry.Register(TestContainer.Open(volume));
        string root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, TreeOverlay.MarkerFileName), handle);

            var node = new FSDir();
            bool handled = TreeOverlay.Populate(node, root);

            Assert.True(handled);

            Assert.DoesNotContain(node.Dirs, d => d.name == "sce_sys");
            Assert.DoesNotContain(node.Files, f => f.name == "sce_sys");

            var eboot = Assert.Single(node.Files);
            Assert.Equal("eboot.bin", eboot.name);
            Assert.Same(node, eboot.Parent);

            var media = Assert.Single(node.Dirs);
            Assert.Equal("Media", media.name);
            Assert.Same(node, media.Parent);
            Assert.Empty(media.Dirs);

            var xbin = Assert.Single(media.Files);
            Assert.Equal("x.bin", xbin.name);
            Assert.Same(media, xbin.Parent);
        }
        finally
        {
            ContainerRegistry.Release(handle);
            Directory.Delete(root, recursive: true);
        }
    }
}

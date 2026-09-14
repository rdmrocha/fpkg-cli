using System.Text;
using Xunit;

namespace FpkgVirtualSource.Tests;

public class ExfatDirectoryTests
{
    private static ExfatReader Reader() => new(new TestVolume()
        .Add("eboot.bin", Encoding.ASCII.GetBytes("ELF-ish payload"))
        .Add("sce_sys/param.json", Encoding.ASCII.GetBytes("{}"))
        .Add("Media/resources.resource", new byte[ 70000 ])
        .Build());

    [Fact]
    public void FindsTopLevelFiles()
    {
        var names = Reader().RootEntries().Where(e => !e.IsDirectory).Select(e => e.Name);
        Assert.Contains("eboot.bin", names);
    }

    [Fact]
    public void FindsSubdirectoriesAndRecursesIntoThem()
    {
        var root = Reader().RootEntries();
        var sceSys = Assert.Single(root, e => e.Name == "sce_sys");
        Assert.True(sceSys.IsDirectory);
        Assert.Contains("param.json", sceSys.Children.Select(c => c.Name));
    }

    [Fact]
    public void BuildsPosixRelativePaths()
    {
        var paths = Reader().EnumerateFiles().Select(e => e.RelativePath).ToList();
        Assert.Contains("sce_sys/param.json", paths);
        Assert.Contains("Media/resources.resource", paths);
        Assert.Contains("eboot.bin", paths);
    }

    [Fact]
    public void RecordsLengthAndContiguityFromTheStreamExtension()
    {
        var f = Reader().EnumerateFiles().Single(e => e.RelativePath == "Media/resources.resource");
        Assert.Equal(70000, f.Length);
        Assert.True(f.NoFatChain);
        Assert.True(f.FirstCluster >= 2);
    }

    [Fact]
    public void EnumerationOrderIsDeterministicAndCaseInsensitive()
    {
        var a = Reader().EnumerateFiles().Select(e => e.RelativePath).ToList();
        var b = Reader().EnumerateFiles().Select(e => e.RelativePath).ToList();
        Assert.Equal(a, b);
        Assert.Equal(a.OrderBy(p => p, StringComparer.OrdinalIgnoreCase), a);
    }

    [Fact]
    public void HandlesNamesLongerThanOneNameEntry()
    {
        var longName = new string('x', 40) + ".dat";
        var r = new ExfatReader(new TestVolume().Add(longName, [1, 2, 3]).Build());
        Assert.Equal(longName, r.EnumerateFiles().Single().Name);
    }

    [Fact]
    public void RootDirectorySpanningMultipleClustersEnumeratesEveryFile()
    {
        // 65536-byte clusters / 32-byte entries = 2048 entries per cluster. Each zero-length
        // file here occupies 3 entries (file + stream + one file-name entry), so 700 files
        // need 2100 root-directory entries -- more than one cluster holds. This exercises the
        // FAT chain lookup for the root directory rather than a single fixed cluster.
        const int fileCount = 700;
        var vol = new TestVolume();
        for (int i = 0; i < fileCount; i++) vol.Add($"f{i:D4}.dat", []);
        var r = new ExfatReader(vol.Build());

        var names = r.EnumerateFiles().Select(e => e.Name).ToList();
        Assert.Equal(fileCount, names.Count);
        for (int i = 0; i < fileCount; i++)
            Assert.Contains($"f{i:D4}.dat", names);
    }
}

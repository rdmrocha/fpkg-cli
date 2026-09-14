using Xunit;

namespace FpkgVirtualSource.Tests;

/// <summary>
/// Pins <see cref="VirtualSource.Enumerate"/>'s behaviour against a real <see cref="Container"/>
/// built from a <see cref="TestVolume"/>. This method is the sole input to a later task that
/// rebuilds the package's inner file tree, so a regression here would silently produce a
/// package containing the wrong files.
/// </summary>
public class VirtualSourceEnumerateTests
{
    private static string Register(TestVolume volume) =>
        ContainerRegistry.Register(TestContainer.Open(volume));

    [Fact]
    public void DoesNotMatchASiblingDirectoryWithASharedPrefix()
    {
        string handle = Register(new TestVolume()
            .Add("Media/a.bin", [1])
            .Add("Media2/b.bin", [2]));
        try
        {
            var names = VirtualSource.Enumerate(VirtualSource.PathFor(handle, "Media"))
                .Select(e => e.RelativePath).ToList();

            Assert.Equal(new[] { "a.bin" }, names);
        }
        finally { ContainerRegistry.Release(handle); }
    }

    [Fact]
    public void SynthesizesEachIntermediateDirectoryExactlyOnce()
    {
        // "A/B/C/*" fakes two levels of nesting the same way the exFAT fixture already does
        // elsewhere: TestVolume treats the full parent-path string as one directory's name,
        // and ExfatReader concatenates it into RelativePath verbatim -- Enumerate only ever
        // looks at the resulting flat RelativePath strings, so this exercises the same
        // directory-synthesis code a genuinely multi-level volume would.
        string handle = Register(new TestVolume()
            .Add("A/B/C/d1.bin", [1])
            .Add("A/B/C/d2.bin", [2])
            .Add("A/B/e.bin", [3]));
        try
        {
            var entries = VirtualSource.Enumerate(VirtualSource.Root(handle)).ToList();
            var dirs = entries.Where(e => e.IsDirectory).Select(e => e.RelativePath).ToList();

            Assert.Equal(new[] { "A", "A/B", "A/B/C" }, dirs.OrderBy(d => d, StringComparer.Ordinal));
            Assert.Equal(dirs.Count, dirs.Distinct(StringComparer.Ordinal).Count());

            var files = entries.Where(e => !e.IsDirectory).Select(e => e.RelativePath).ToList();
            Assert.Equal(
                new[] { "A/B/C/d1.bin", "A/B/C/d2.bin", "A/B/e.bin" },
                files.OrderBy(f => f, StringComparer.Ordinal));
        }
        finally { ContainerRegistry.Release(handle); }
    }

    [Fact]
    public void EntriesAreRelativeToTheRequestedRootNotTheVolumeRoot()
    {
        string handle = Register(new TestVolume()
            .Add("A/B/C/d1.bin", [1])
            .Add("A/B/e.bin", [2, 3]));
        try
        {
            var entries = VirtualSource.Enumerate(VirtualSource.PathFor(handle, "A/B")).ToList();
            var paths = entries.Select(e => e.RelativePath).ToList();

            Assert.DoesNotContain(paths, p => p.StartsWith("A/B", StringComparison.Ordinal));
            Assert.Contains(new VirtualEntry("C", IsDirectory: true, Length: 0), entries);
            Assert.Contains(new VirtualEntry("C/d1.bin", IsDirectory: false, Length: 1), entries);
            Assert.Contains(new VirtualEntry("e.bin", IsDirectory: false, Length: 2), entries);
        }
        finally { ContainerRegistry.Release(handle); }
    }

    [Fact]
    public void EmptyRootListsTheWholeVolume()
    {
        string handle = Register(new TestVolume()
            .Add("eboot.bin", [1])
            .Add("Media/x.bin", [2, 3]));
        try
        {
            var paths = VirtualSource.Enumerate(VirtualSource.Root(handle))
                .Select(e => e.RelativePath).ToList();

            Assert.Contains("eboot.bin", paths);
            Assert.Contains("Media", paths);
            Assert.Contains("Media/x.bin", paths);
        }
        finally { ContainerRegistry.Release(handle); }
    }

    [Fact]
    public void EnumerationIsDeterministic()
    {
        string handle = Register(new TestVolume()
            .Add("Media/b.bin", [1])
            .Add("Media/a.bin", [2])
            .Add("eboot.bin", [3]));
        try
        {
            string root = VirtualSource.Root(handle);
            var first = VirtualSource.Enumerate(root).ToList();
            var second = VirtualSource.Enumerate(root).ToList();

            Assert.Equal(first, second);
        }
        finally { ContainerRegistry.Release(handle); }
    }
}

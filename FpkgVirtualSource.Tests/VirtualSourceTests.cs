using System.Text;
using Xunit;

namespace FpkgVirtualSource.Tests;

public class VirtualSourceTests
{
    [Theory]
    [InlineData("ffpfsc:a1b2c3d4:/Media/x.bin", true)]
    [InlineData("/Users/me/game/eboot.bin", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RecognisesVirtualPaths(string? path, bool expected) =>
        Assert.Equal(expected, VirtualSource.IsVirtual(path));

    [Fact]
    public void ComposesAndParsesPaths()
    {
        string p = VirtualSource.PathFor("a1b2c3d4", "Media/x.bin");
        Assert.Equal("ffpfsc:a1b2c3d4:/Media/x.bin", p);
        Assert.True(VirtualSource.IsVirtual(p));
        Assert.Equal("ffpfsc:a1b2c3d4:/", VirtualSource.Root("a1b2c3d4"));
    }

    [Fact]
    public void OpenOrFileFallsBackToTheRealFilesystem()
    {
        string tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "hello");
            using var s = VirtualSource.OpenOrFile(tmp);
            using var r = new StreamReader(s);
            Assert.Equal("hello", r.ReadToEnd());
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void UnknownHandleFailsLoudly()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => VirtualSource.Open("ffpfsc:deadbeef:/x"));
        Assert.Contains("deadbeef", ex.Message);
    }
}

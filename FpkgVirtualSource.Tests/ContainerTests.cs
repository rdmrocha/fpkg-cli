using System.Text;
using Xunit;

namespace FpkgVirtualSource.Tests;

public class ContainerTests
{
    private static byte[] Json => Encoding.ASCII.GetBytes("{}");

    [Fact]
    public void FindAppRootRequiresAPathSegmentBoundaryBeforeTheSuffix()
    {
        // "Foosce_sys/param.json" ends with the literal suffix "sce_sys/param.json" but the
        // character right before it is 'o', not '/' -- this is a directory named
        // "Foosce_sys", not an app root named "Foo". A naive EndsWith match would report "Foo".
        using var container = TestContainer.Open(
            new TestVolume().Add("Foosce_sys/param.json", Json));

        Assert.Null(container.FindAppRoot());
    }

    [Fact]
    public void FindAppRootIsTheEmptyStringWhenParamJsonIsAtTheVolumeRoot()
    {
        using var container = TestContainer.Open(
            new TestVolume().Add("sce_sys/param.json", Json));

        Assert.Equal("", container.FindAppRoot());
    }

    [Fact]
    public void FindAppRootFindsANestedRoot()
    {
        using var container = TestContainer.Open(
            new TestVolume()
                .Add("MyGame/sce_sys/param.json", Json)
                .Add("MyGame/eboot.bin", "ELF"u8.ToArray()));

        Assert.Equal("MyGame", container.FindAppRoot());
    }

    [Fact]
    public void FindAppRootIsNullWhenThereAreTwoAppRoots()
    {
        using var container = TestContainer.Open(
            new TestVolume()
                .Add("A/sce_sys/param.json", Json)
                .Add("B/sce_sys/param.json", Json));

        Assert.Null(container.FindAppRoot());
    }
}

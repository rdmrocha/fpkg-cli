using Xunit;

namespace FpkgVirtualSource.Tests;

/// <summary>
/// <see cref="WindowStream"/> is what lets LibProsperoPkg write the inner pfs_image.dat straight
/// into the final package instead of into a spool file: it presents the bytes from
/// <c>baseOffset</c> onwards as if they were a file of their own, so every offset the library
/// computes from zero lands at <c>baseOffset + offset</c>.
///
/// <para>The reserved head — the package's FIH header range — must survive every operation,
/// including the Create-mode truncation. That is what most of these tests are about.</para>
/// </summary>
public class WindowStreamTests
{
    private static string TempFile(byte[] contents)
    {
        string path = Path.Combine(Path.GetTempPath(), "win-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, contents);
        return path;
    }

    [Fact]
    public void ReadsAndWritesAreOffsetByTheBase()
    {
        string path = TempFile([.. Enumerable.Repeat((byte)0xAA, 64), .. new byte[] { 1, 2, 3, 4 }]);
        using (var w = new WindowStream(File.Open(path, FileMode.Open, FileAccess.ReadWrite), 64, false))
        {
            Assert.Equal(0, w.Position);
            Assert.Equal(4, w.Length);
            var buf = new byte[4];
            w.ReadExactly(buf);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, buf);

            w.Position = 0;
            w.Write([9, 9], 0, 2);
        }
        byte[] all = File.ReadAllBytes(path);
        Assert.Equal(0xAA, all[63]);            // the reserved head is untouched
        Assert.Equal(new byte[] { 9, 9, 3, 4 }, all[64..]);
    }

    [Fact]
    public void SetLengthIsRelativeToTheBaseAndNeverEatsTheHead()
    {
        string path = TempFile(new byte[128]);
        using (var w = new WindowStream(File.Open(path, FileMode.Open, FileAccess.ReadWrite), 64, false))
        {
            w.SetLength(16);
            Assert.Equal(16, w.Length);
        }
        Assert.Equal(80, new FileInfo(path).Length);
    }

    [Fact]
    public void TruncateOnOpenClearsOnlyTheWindow()
    {
        string path = TempFile([.. Enumerable.Repeat((byte)0xAA, 64), .. new byte[] { 7, 7, 7, 7 }]);
        using (new WindowStream(File.Open(path, FileMode.Open, FileAccess.ReadWrite), 64, true)) { }
        byte[] all = File.ReadAllBytes(path);
        Assert.Equal(64, all.Length);
        Assert.Equal(0xAA, all[63]);
    }

    [Fact]
    public void HighWaterMarkRemembersTheLargestLengthSeen()
    {
        string path = TempFile(new byte[64]);
        using var w = new WindowStream(File.Open(path, FileMode.Open, FileAccess.ReadWrite), 64, true);
        w.Write(new byte[100], 0, 100);
        w.SetLength(40);
        Assert.Equal(40, w.Length);
        Assert.Equal(100, w.HighWaterMark);
    }

    [Fact]
    public void ReadsClampAtTheEndOfTheWindow()
    {
        string path = TempFile([.. new byte[64], .. new byte[] { 1, 2 }]);
        using var w = new WindowStream(File.Open(path, FileMode.Open, FileAccess.Read), 64, false);
        var buf = new byte[8];
        Assert.Equal(2, w.Read(buf, 0, 8));
        Assert.Equal(0, w.Read(buf, 0, 8));
    }
}

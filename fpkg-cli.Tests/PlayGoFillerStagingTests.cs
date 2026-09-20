using System.Security.Cryptography;
using Fpkg.Cli.PlayGo;
using FpkgVirtualSource;
using Xunit;

/// <summary>
/// What <c>--playgo-fix</c> actually stages, and — the part that can go wrong quietly — which paths
/// it hands to the site 10 dedup bypass. Staging the fillers and widening the bypass are one step
/// here on purpose: a filler that is written but not registered de-duplicates away, and a path that
/// is registered but not written would be a bypass granted to a file the tool did not place.
/// </summary>
public class PlayGoFillerStagingTests : IDisposable
{
    private readonly string _source = Directory.CreateTempSubdirectory("fpkg-filler-src-").FullName;
    private readonly string _stage = Path.Combine(
        Directory.CreateTempSubdirectory("fpkg-filler-stage-").FullName, "stage");

    public PlayGoFillerStagingTests()
    {
        Directory.CreateDirectory(Path.Combine(_source, "sce_sys"));
        File.WriteAllText(Path.Combine(_source, "Media.txt"), "not a filler");
        PlayGoFillerDedup.Reset();
    }

    public void Dispose()
    {
        PlayGoFillerDedup.Reset();
        foreach (string dir in new[] { _source, Path.GetDirectoryName(_stage)! })
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // Staging is IN PLACE: the fillers are written into the source tree and removed again on
    // dispose, so this reads the source, not the backup directory. _stage is where an interrupted
    // run leaves its recovery marker.
    private string[] Staged() =>
        Directory.GetFiles(Path.Combine(_source, PlayGoFillers.Directory)).Order().ToArray();

    /// <summary>
    /// The default: Sony's literal form — 31 files, 1 MiB each, every one byte-identical to the
    /// others — and every one of them registered, so site 10 gives it its own stored copy.
    /// </summary>
    [Fact]
    public void ZeroFillersAreSonysExactFormAndAreAllRegistered()
    {
        PlayGoFillers.Stage(_source, _stage, distinct: false);

        var files = Staged();
        Assert.Equal(31, files.Length);

        var expected = SHA256.HashData(new byte[PlayGoFillers.ZeroPayloadSize]);
        foreach (string file in files)
        {
            Assert.Equal(1024 * 1024, new FileInfo(file).Length);
            Assert.Equal(expected, SHA256.HashData(File.ReadAllBytes(file)));
            Assert.True(PlayGoFillerDedup.IsFiller(
                $"/{PlayGoFillers.Directory}/{Path.GetFileName(file)}"));
        }

        Assert.Equal(31, PlayGoFillerDedup.Count);
        // One per chunk, 1-based, chunk 1 first.
        Assert.StartsWith("01-", Path.GetFileName(files[0]), StringComparison.Ordinal);
        Assert.StartsWith("31-", Path.GetFileName(files[30]), StringComparison.Ordinal);
    }

    /// <summary>
    /// The fallback stages DIFFERENT content and registers nothing: it makes the fillers distinct
    /// by content, so the bypass must stay empty and every file in the package — fillers included —
    /// takes the stock de-duplicating path.
    /// </summary>
    [Fact]
    public void DistinctFillersRegisterNothing()
    {
        PlayGoFillers.Stage(_source, _stage, distinct: true);

        var files = Staged();
        Assert.Equal(31, files.Length);
        Assert.All(files, f => Assert.Equal(PlayGoFillers.DistinctPayloadSize,
                                            new FileInfo(f).Length));
        Assert.Equal(31, files.Select(f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))))
                              .Distinct().Count());

        Assert.Equal(0, PlayGoFillerDedup.Count);
        Assert.All(files, f => Assert.False(PlayGoFillerDedup.IsFiller(
            $"/{PlayGoFillers.Directory}/{Path.GetFileName(f)}")));
    }

    /// <summary>
    /// Nothing the SOURCE carries is ever registered — only the 31 files this tool writes. A
    /// bypass that leaked onto a real game file would change how that file is stored.
    /// </summary>
    [Fact]
    public void NoSourceFileIsEverExempt()
    {
        PlayGoFillers.Stage(_source, _stage, distinct: false);

        Assert.False(PlayGoFillerDedup.IsFiller("/Media.txt"));
        Assert.False(PlayGoFillerDedup.IsFiller("/eboot.bin"));
        Assert.False(PlayGoFillerDedup.IsFiller("/sce_sys/param.json"));
        // A 32nd name of the right shape was never staged, so it is not exempt either.
        Assert.False(PlayGoFillerDedup.IsFiller("/playgo-languages/31-xx-XX.bin"));
    }

    /// <summary>
    /// A second staging replaces the allow-list rather than accumulating onto it. In-place staging
    /// refuses a source that already holds a playgo-languages directory unless forced, so the
    /// second call is the forced one -- which is the case that could double the list.
    /// </summary>
    [Fact]
    public void StagingTwiceDoesNotAccumulate()
    {
        PlayGoFillers.Stage(_source, _stage, distinct: false);
        PlayGoFillers.Stage(_source, _stage, distinct: false, force: true);
        Assert.Equal(31, PlayGoFillerDedup.Count);
    }
}

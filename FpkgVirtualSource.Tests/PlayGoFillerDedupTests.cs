using Xunit;

namespace FpkgVirtualSource.Tests;

/// <summary>
/// <see cref="PlayGoFillerDedup"/> is the whole decision behind IL site 10, so a mistake here is a
/// silent change to how EVERY file in EVERY package is compressed. The three load-bearing cases are
/// the first three tests: a normal file keeps de-duplication, an explicit <c>false</c> is never
/// turned back into <c>true</c>, and only a registered filler is overridden — and it says so.
///
/// <para>
/// The state is process-global (the patched library calls into it through static IL), so every test
/// resets it and the class is not parallelised with itself, which xUnit already guarantees for
/// tests in one class.
/// </para>
/// </summary>
public class PlayGoFillerDedupTests : IDisposable
{
    private readonly List<string> _log = [];

    public PlayGoFillerDedupTests()
    {
        PlayGoFillerDedup.Reset();
        PlayGoFillerDedup.Log = _log.Add;
    }

    public void Dispose()
    {
        PlayGoFillerDedup.Reset();
        PlayGoFillerDedup.Log = Console.Out.WriteLine;
        GC.SuppressFinalize(this);
    }

    /// <summary>Case 1: an ordinary file with dedup on keeps it, and nothing is said about it.</summary>
    [Theory]
    [InlineData("/eboot.bin")]
    [InlineData("/Media/RuntimeInitializeOnLoads.json")]
    [InlineData("/sce_sys/param.json")]
    [InlineData("/sce_module/libc.prx")]
    // Near misses, all of which MUST stay de-duplicating: the bypass is membership, not a pattern.
    [InlineData("/playgo-languages/01-en-US.bin")]      // right shape, never registered
    [InlineData("/playgo-languages/32-xx-XX.bin")]
    [InlineData("/playgo-languages/README.txt")]
    [InlineData("/other/playgo-languages/01-en-US.bin")]
    [InlineData(null)]
    public void ANonFillerWithDedupEnabledStaysEnabled(string? path)
    {
        PlayGoFillerDedup.Register("playgo-languages/07-nl-NL.bin");

        Assert.False(PlayGoFillerDedup.ShouldDisableDedup(path, deduplicateRequested: true));
        Assert.Empty(_log);
        Assert.Equal(0, PlayGoFillerDedup.OverriddenCount);
        Assert.Equal(0, PlayGoFillerDedup.AlreadyDisabledCount);
    }

    /// <summary>
    /// Case 2: the explicit-false call site. An optional parameter's default is baked in at the
    /// CALL site, so the callee cannot tell a deliberate <c>true</c> from a defaulted one — which
    /// is exactly why the shim may only ever narrow. A caller that asked for no de-duplication gets
    /// no de-duplication, filler or not, and the value is never flipped back.
    /// </summary>
    [Theory]
    [InlineData("/eboot.bin")]
    [InlineData("/sce_sys/param.json")]
    [InlineData(null)]
    public void ANonFillerOnTheExplicitFalsePathStaysFalse(string? path)
    {
        PlayGoFillerDedup.Register("playgo-languages/07-nl-NL.bin");

        Assert.False(PlayGoFillerDedup.ShouldDisableDedup(path, deduplicateRequested: false));
        Assert.Empty(_log);
        Assert.Equal(0, PlayGoFillerDedup.OverriddenCount);
        Assert.Equal(0, PlayGoFillerDedup.AlreadyDisabledCount);
    }

    /// <summary>Case 3: a registered filler with dedup on is overridden, and it is announced.</summary>
    [Theory]
    [InlineData("/playgo-languages/01-en-US.bin")]
    [InlineData("playgo-languages/01-en-US.bin")]
    public void AFillerWithDedupEnabledIsDisabledAndLogged(string path)
    {
        PlayGoFillerDedup.Register("playgo-languages/01-en-US.bin");

        Assert.True(PlayGoFillerDedup.ShouldDisableDedup(path, deduplicateRequested: true));

        string line = Assert.Single(_log);
        Assert.Contains("playgo-languages/01-en-US.bin", line, StringComparison.Ordinal);
        Assert.Contains("(was enabled)", line, StringComparison.Ordinal);
        Assert.Equal(1, PlayGoFillerDedup.OverriddenCount);
        Assert.Equal(0, PlayGoFillerDedup.AlreadyDisabledCount);
    }

    /// <summary>
    /// A filler met with dedup ALREADY off is not an override, and must be reported distinctly:
    /// it means the library routed a filler through a call site site 10 was not derived against.
    /// </summary>
    [Fact]
    public void AFillerAlreadyDisabledByTheCallerIsReportedButNotCounted()
    {
        PlayGoFillerDedup.Register("playgo-languages/01-en-US.bin");

        Assert.False(PlayGoFillerDedup.ShouldDisableDedup("/playgo-languages/01-en-US.bin",
                                                          deduplicateRequested: false));

        string line = Assert.Single(_log);
        Assert.Contains("ALREADY disabled", line, StringComparison.Ordinal);
        Assert.Equal(0, PlayGoFillerDedup.OverriddenCount);
        Assert.Equal(1, PlayGoFillerDedup.AlreadyDisabledCount);
        Assert.Contains("must be re-derived", PlayGoFillerDedup.Summary(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The default state is "no file anywhere is exempt". Every build that does not stage fillers
    /// therefore runs against a bypass that cannot fire, which is what makes the leg inert.
    /// </summary>
    [Fact]
    public void NothingIsExemptUntilSomethingIsRegistered()
    {
        Assert.Equal(0, PlayGoFillerDedup.Count);
        Assert.False(PlayGoFillerDedup.IsFiller("/playgo-languages/01-en-US.bin"));
        Assert.False(PlayGoFillerDedup.ShouldDisableDedup("/playgo-languages/01-en-US.bin", true));
        Assert.True(PlayGoFillerDedup.CompressionCacheAllowed("/playgo-languages/01-en-US.bin"));
    }

    /// <summary>Each file announces once, however many blocks it is split into.</summary>
    [Fact]
    public void EachFillerIsAnnouncedOnce()
    {
        PlayGoFillerDedup.Register("playgo-languages/01-en-US.bin");
        for (int block = 0; block < 4; block++)
            Assert.True(PlayGoFillerDedup.ShouldDisableDedup("/playgo-languages/01-en-US.bin", true));
        // The rooted and unrooted spellings are one file, not two.
        Assert.True(PlayGoFillerDedup.ShouldDisableDedup("playgo-languages/01-en-US.bin", true));

        Assert.Single(_log);
        Assert.Equal(1, PlayGoFillerDedup.OverriddenCount);
    }

    /// <summary>The compression-cache conjunct tracks membership and nothing else.</summary>
    [Fact]
    public void OnlyAFillerSkipsTheCompressionCache()
    {
        PlayGoFillerDedup.Register("playgo-languages/01-en-US.bin");

        Assert.False(PlayGoFillerDedup.CompressionCacheAllowed("/playgo-languages/01-en-US.bin"));
        Assert.True(PlayGoFillerDedup.CompressionCacheAllowed("/eboot.bin"));
        Assert.True(PlayGoFillerDedup.CompressionCacheAllowed("/playgo-languages/02-ja-JP.bin"));
        Assert.True(PlayGoFillerDedup.CompressionCacheAllowed(null));
    }

    /// <summary>
    /// Registration is the narrow gate, so it refuses anything that is not a filler path rather
    /// than quietly widening the bypass.
    /// </summary>
    [Theory]
    [InlineData("eboot.bin")]
    [InlineData("playgo-languages/00-en-US.bin")]
    [InlineData("playgo-languages/32-en-US.bin")]
    [InlineData("playgo-languages/1-en-US.bin")]
    [InlineData("playgo-languages/01-en-US.dat")]
    [InlineData("playgo-languages/01-en-US.bin/x")]
    [InlineData("sce_sys/playgo-languages/01-en-US.bin")]
    [InlineData("PLAYGO-LANGUAGES/01-en-US.bin")]
    public void RegisterRefusesAnythingThatIsNotAFiller(string path) =>
        Assert.Throws<ArgumentException>(() => PlayGoFillerDedup.Register(path));

    /// <summary>The 31 real names Sony's generator produces all register.</summary>
    [Theory]
    [InlineData("playgo-languages/01-en-US.bin")]
    [InlineData("playgo-languages/11-zh-Hant.bin")]
    [InlineData("playgo-languages/21-es-419.bin")]
    [InlineData("playgo-languages/31-uk-UA.bin")]
    public void RegisterAcceptsEveryShapeTheGeneratorProduces(string path)
    {
        PlayGoFillerDedup.Register(path);
        Assert.Equal(1, PlayGoFillerDedup.Count);
        Assert.True(PlayGoFillerDedup.IsFiller(path));
        Assert.True(PlayGoFillerDedup.IsFiller("/" + path));
    }
}

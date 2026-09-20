using Fpkg.Tests;
using System.Security.Cryptography;
using Fpkg.Cli;
using Xunit;

/// <summary>
/// The fixtures are two builds of the same title — one by fpkg 0.6.8, one by the SyntheticPackage.Path
/// Publishing Tools — and every assertion below is against an independently parsed
/// playgo-ficm.dat, not against this tool's own earlier output.
///
/// <para>
/// Deliberately plain <c>Fact</c>s, not <c>SkippableFact</c>s: in a bare worktree a skip-on-missing
/// -fixture suite reports "passed" having verified nothing at all, which is worse than red.
/// <see cref="FixturesArePresent"/> fails loudly instead.
/// </para>
/// </summary>
public class InnerListTests
{


    /// <summary>The FICM in this package records 23 inner files.</summary>
    [Fact]
    public void TheListingHoldsEverySourceFileAndOmitsDigestsUnlessAsked()
    {
        var files = InnerList.Enumerate(SyntheticPackage.Path, SyntheticPackage.Passcode, sha256: false).ToList();

        foreach (var (path, _) in SyntheticPackage.Contents)
            Assert.Contains(files, f => f.Path == path);
        Assert.All(files, f => Assert.Null(f.Sha256));
    }

    /// <summary>
    /// The FICM in the SyntheticPackage.Path build records 52 inner files, 31 of them one-mebibyte,
    /// entirely-zero language chunks. Zero-filled is asserted through the digest rather than by
    /// reading the bytes: all 31 must hash to the digest of 1 MiB of zeros, computed here.
    /// </summary>
    [Fact]
    public void EveryLanguageChunkIsOneMebibyteOfZeros()
    {
        var files = InnerList.Enumerate(SyntheticPackage.Path, SyntheticPackage.Passcode, sha256: true).ToList();

        var langs = files.Where(f => f.Path.StartsWith("playgo-languages/")).ToList();
        Assert.Equal(31, langs.Count);
        Assert.All(langs, f => Assert.Matches(@"^playgo-languages/\d\d-[\w-]+\.bin$", f.Path));
        Assert.All(langs, f => Assert.Equal(1048576, f.Size));

        string zeros = Convert.ToHexString(SHA256.HashData(new byte[1048576])).ToLowerInvariant();
        Assert.All(langs, f => Assert.Equal(zeros, f.Sha256));
        Assert.Single(langs.Select(f => f.Sha256).Distinct());
    }

    /// <summary>

    /// <summary>
    /// The sink is the whole design: CopyTo streams into it and the bytes are thrown away, so if
    /// it computed the digest wrongly every comparison built on this would be quietly meaningless.
    /// Checked against ReadAllBytes, which is safe here only because keystone is 96 bytes.
    /// </summary>
    [Fact]
    public void TheStreamedDigestMatchesTheFilesActualBytes()
    {
        var keystone = InnerList.Enumerate(SyntheticPackage.Path, SyntheticPackage.Passcode, sha256: true, filter: "keystone")
            .Single();

        Assert.Equal(96, keystone.Size);
        Assert.Equal(InnerList.DigestOfSmallFile(SyntheticPackage.Path, SyntheticPackage.Passcode, keystone.Path),
                     keystone.Sha256);
    }

    [Fact]
    public void TheFilterNarrowsTheListingWithoutChangingIt()
    {
        var all = InnerList.Enumerate(SyntheticPackage.Path, SyntheticPackage.Passcode, sha256: false).ToList();
        var some = InnerList.Enumerate(SyntheticPackage.Path, SyntheticPackage.Passcode, sha256: false, filter: "playgo").ToList();

        Assert.Equal(31, some.Count);
        Assert.Equal(all.Where(f => f.Path.Contains("playgo")).ToList(), some);
    }
}

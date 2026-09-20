using Fpkg.Tests;
using System.Security.Cryptography;
using Fpkg.Cli;
using Xunit;

/// <summary>
/// The offsets are only worth anything if they point at the right bytes, so the load-bearing test
/// here does not check arithmetic against more arithmetic: it reads the predicted range straight
/// out of the .pkg with a plain FileStream and hashes it. These fixtures have a plaintext outer
/// PFS, which is what makes that comparison possible; the arithmetic itself is unaffected by
/// encryption, because XTS is length preserving and block aligned, so an encrypted package has
/// the same offsets with different bytes.
/// </summary>
public class InnerOffsetsTests
{

    [Fact]
    public void APlacedFilesPackageRangeHoldsExactlyThatFilesBytes()
    {
        var placed = InnerList.Enumerate(SyntheticPackage.Path, SyntheticPackage.Passcode, sha256: true, offsets: true)
            .Where(f => f.MapNote is null && f.Size > 1_000_000)
            .ToList();
        Assert.NotEmpty(placed);   // a fixture with nothing stored verbatim would test nothing

        foreach (var f in placed)
        {
            Assert.Equal(f.Size, f.PkgEnd - f.PkgStart);
            Assert.Equal(f.Sha256, HashPackageRange(SyntheticPackage.Path, f.PkgStart, f.Size));
        }
    }

    /// <summary>
    /// Every file's span footprint must contain its verbatim range when it has one — the footprint
    /// is defined as a superset, and a superset that does not contain the thing it covers would
    /// make every --range answer wrong in a way no other assertion here would catch.
    /// </summary>
    [Fact]
    public void TheSpanFootprintContainsTheVerbatimRange()
    {
        foreach (var f in InnerList.Enumerate(SyntheticPackage.Path, SyntheticPackage.Passcode, sha256: false, offsets: true)
                     .Where(f => f.MapNote is null))
        {
            Assert.True(f.PkgSpanStart <= f.PkgStart && f.PkgSpanEnd >= f.PkgEnd,
                        $"{f.Path}: [{f.PkgStart},{f.PkgEnd}) escapes its span footprint " +
                        $"[{f.PkgSpanStart},{f.PkgSpanEnd})");
        }
    }

    /// <summary>
    /// The 31 all-zero language chunks are the case the whole "does real data live here" question
    /// turns on: NAPS de-duplicates them, so a mebibyte of logical zeros costs a few bytes of
    /// package. If that ever stopped being true these would report a megabyte-wide footprint and
    /// a range query over that region would mean something completely different.
    /// </summary>
    [Fact]
    public void TheZeroLanguageChunksBarelyExistInThePackage()
    {
        var langs = InnerList.Enumerate(SyntheticPackage.Path, SyntheticPackage.Passcode, sha256: false, offsets: true,
                                        filter: "playgo-languages/").ToList();

        Assert.Equal(31, langs.Count);
        Assert.All(langs, f => Assert.Equal("naps-zero-dedup", f.MapNote));
        Assert.All(langs, f => Assert.InRange(f.PkgSpanEnd - f.PkgSpanStart, 1, 64));
    }

    [Fact]
    public void ARangeQueryReturnsExactlyTheFilesTouchingIt()
    {
        var all = InnerList.Enumerate(SyntheticPackage.Path, SyntheticPackage.Passcode, sha256: false, offsets: true).ToList();
        // The largest file, so its span is comfortably its own rather than shared with a neighbour.
        var subject = all.Where(f => f.PkgSpanStart >= 0).MaxBy(f => f.Size)!;

        var hit = InnerList.Enumerate(SyntheticPackage.Path, SyntheticPackage.Passcode, sha256: false,
                                      range: (subject.PkgSpanStart, subject.PkgSpanStart + 1)).ToList();
        Assert.Contains(hit, f => f.Path == subject.Path);

        // Half-open: a window ending exactly where the file starts must not match it.
        var miss = InnerList.Enumerate(SyntheticPackage.Path, SyntheticPackage.Passcode, sha256: false,
                                       range: (subject.PkgSpanStart - 1, subject.PkgSpanStart)).ToList();
        Assert.DoesNotContain(miss, f => f.Path == subject.Path);
    }

    /// <summary>Offsets must never change what the listing says about the files themselves.</summary>
    [Fact]
    public void AskingForOffsetsDoesNotChangeTheListing()
    {
        var without = InnerList.Enumerate(SyntheticPackage.Path, SyntheticPackage.Passcode, sha256: false).ToList();
        var with = InnerList.Enumerate(SyntheticPackage.Path, SyntheticPackage.Passcode, sha256: false, offsets: true).ToList();

        Assert.Equal(without.Select(f => (f.Path, f.Size, f.PhysicalSize, f.Compressed)),
                     with.Select(f => (f.Path, f.Size, f.PhysicalSize, f.Compressed)));
    }

    /// <summary>Reads the range in 1 MiB pieces — the test must not be the thing that OOMs.</summary>
    private static string HashPackageRange(string path, long start, long length)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                      1 << 20, FileOptions.SequentialScan);
        fs.Position = start;
        var buffer = new byte[1 << 20];
        while (length > 0)
        {
            int want = (int)Math.Min(length, buffer.Length);
            fs.ReadExactly(buffer, 0, want);
            hash.AppendData(buffer, 0, want);
            length -= want;
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

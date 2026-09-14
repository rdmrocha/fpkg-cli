using System.Security.Cryptography;
using System.Text;
using Fpkg.Cli;
using Xunit;

namespace FpkgVirtualSource.Tests;

/// <summary>
/// Covers the branching in <see cref="PatchStamp.Select"/>, which LibraryResolver uses to decide
/// which LibProsperoPkg to load and which patch legs it carries.
///
/// This is not a diagnostics nicety any more. <see cref="PatchSelection.FfpfscApplied"/> is the
/// stamp's record of whether the virtual-source leg went in, and without that leg a container
/// build runs an unpatched <c>Populate</c>, never overlays the tree, and writes an EMPTY package
/// that still passes verification. So the predicate itself is pinned here, case by case —
/// including the one that actually bit: <c>./fpkg patch --oodle</c> after a full run, which
/// rewrites the patched DLL and stamp with the oodle leg only.
///
/// Everything is exercised against a throwaway release folder, so no test touches the real one.
/// </summary>
public sealed class PatchStampTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fpkg-stamp-").FullName;
    private readonly List<string> _warnings = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    private void Warn(string message) => _warnings.Add(message);

    private string Path_(string name) => Path.Combine(_dir, name);

    /// <summary>Writes a stock LibProsperoPkg.dll with arbitrary content and returns its SHA-256.</summary>
    private string WriteStock(string content = "stock")
    {
        File.WriteAllText(Path_(PatchStamp.StockDllName), content);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    private void WritePatched(string? stampJson)
    {
        File.WriteAllText(Path_(PatchStamp.UnifiedDllName), "patched");
        if (stampJson is not null) File.WriteAllText(Path_(PatchStamp.UnifiedStampName), stampJson);
    }

    private static string Stamp(string sourceSha, params string[] patches) =>
        $"{{\"source\":\"{sourceSha}\",\"patches\":[{string.Join(",", patches.Select(p => $"\"{p}\""))}]}}";

    // ---- 1. nothing patched -------------------------------------------------------------

    [Fact]
    public void StockOnlyReleaseFolderSelectsNothingAndStaysSilent()
    {
        WriteStock();

        var selection = PatchStamp.Select(_dir, Warn);

        Assert.Null(selection.Path);
        Assert.Empty(selection.Patches);
        Assert.False(selection.FfpfscApplied);
        Assert.False(selection.OodleApplied);
        Assert.Empty(_warnings);
    }

    // ---- 2. patched, current, both legs -------------------------------------------------

    [Fact]
    public void CurrentStampWithBothPatchesSelectsThePatchedDllAndReportsBothLegs()
    {
        string sha = WriteStock();
        WritePatched(Stamp(sha, "oodle", "ffpfsc"));

        var selection = PatchStamp.Select(_dir, Warn);

        Assert.Equal(Path_(PatchStamp.UnifiedDllName), selection.Path);
        Assert.Equal(new[] { "oodle", "ffpfsc" }, selection.Patches);
        Assert.True(selection.FfpfscApplied);
        Assert.True(selection.OodleApplied);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void FfpfscOnlyStampReportsFfpfscAndNotOodle()
    {
        string sha = WriteStock();
        WritePatched(Stamp(sha, "ffpfsc"));

        var selection = PatchStamp.Select(_dir, Warn);

        Assert.True(selection.FfpfscApplied);
        Assert.False(selection.OodleApplied);
        Assert.Empty(_warnings);
    }

    // ---- 3. `./fpkg patch --oodle` after a full run: the silent-disarm case ----------------

    [Fact]
    public void OodleOnlyStampSelectsThePatchedDllButReportsNoFfpfscLeg()
    {
        string sha = WriteStock();
        WritePatched(Stamp(sha, "oodle"));

        var selection = PatchStamp.Select(_dir, Warn);

        // The DLL is still preferred — it is a valid, current patched module — but the leg that
        // makes container builds correct is gone, and that is what the CLI must refuse on.
        Assert.Equal(Path_(PatchStamp.UnifiedDllName), selection.Path);
        Assert.True(selection.OodleApplied);
        Assert.False(selection.FfpfscApplied);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void AnEmptyPatchesArrayReportsNeitherLeg()
    {
        string sha = WriteStock();
        WritePatched(Stamp(sha));

        var selection = PatchStamp.Select(_dir, Warn);

        Assert.Equal(Path_(PatchStamp.UnifiedDllName), selection.Path);
        Assert.False(selection.FfpfscApplied);
        Assert.False(selection.OodleApplied);
    }

    [Fact]
    public void PatchNamesAreMatchedCaseInsensitively()
    {
        string sha = WriteStock();
        WritePatched(Stamp(sha, "FFPFSC"));

        Assert.True(PatchStamp.Select(_dir, Warn).FfpfscApplied);
    }

    // ---- 4. stale ------------------------------------------------------------------------

    [Fact]
    public void AStaleStampIsRejectedLoudlyAndFallsThroughToStock()
    {
        WriteStock();
        // Recorded against a different stock DLL than the one now on disk: exactly what an
        // upstream LibProsperoPkg.dll update produces.
        WritePatched(Stamp(new string('a', 64), "oodle", "ffpfsc"));

        var selection = PatchStamp.Select(_dir, Warn);

        Assert.Null(selection.Path);
        Assert.False(selection.FfpfscApplied);
        Assert.Contains(_warnings, w => w.Contains("stale patched copy") && w.Contains("./fpkg patch"));
    }

    [Fact]
    public void AStalePatchedDllStillFallsThroughToACurrentLegacyOodleDll()
    {
        string sha = WriteStock();
        WritePatched(Stamp(new string('a', 64), "oodle", "ffpfsc"));
        File.WriteAllText(Path_(PatchStamp.LegacyOodleDllName), "legacy");
        File.WriteAllText(Path_(PatchStamp.LegacyOodleStampName), $"{{\"sourceSha256\":\"{sha}\"}}");

        var selection = PatchStamp.Select(_dir, Warn);

        Assert.Equal(Path_(PatchStamp.LegacyOodleDllName), selection.Path);
        Assert.True(selection.OodleApplied);
        // The legacy pipeline never had a virtual-source leg, so container builds stay refused.
        Assert.False(selection.FfpfscApplied);
    }

    [Fact]
    public void AMissingStampIsRejectedLoudly()
    {
        WriteStock();
        WritePatched(stampJson: null);

        var selection = PatchStamp.Select(_dir, Warn);

        Assert.Null(selection.Path);
        Assert.Contains(_warnings, w => w.Contains(PatchStamp.UnifiedStampName) && w.Contains("missing"));
    }

    [Fact]
    public void APatchedDllWithNoStockBesideItIsRejectedLoudly()
    {
        WritePatched(Stamp(new string('a', 64), "ffpfsc"));

        var selection = PatchStamp.Select(_dir, Warn);

        Assert.Null(selection.Path);
        Assert.Contains(_warnings, w => w.Contains("is missing from this release folder"));
    }

    // ---- 5. malformed --------------------------------------------------------------------

    [Theory]
    // Not JSON at all.
    [InlineData("{ this is not json")]
    // Valid JSON, no 'patches' member.
    [InlineData("{\"source\":\"x\"}")]
    // 'patches' present but not an array.
    [InlineData("{\"source\":\"SHA\",\"patches\":\"ffpfsc\"}")]
    // An array holding something that is not a string: this is the shape that used to make
    // `fpkg version` throw, because its own copy of the parse had no try/catch around it.
    [InlineData("{\"source\":\"SHA\",\"patches\":[{\"name\":\"ffpfsc\"}]}")]
    [InlineData("{\"source\":\"SHA\",\"patches\":[7]}")]
    public void AMalformedStampIsRejectedLoudlyRatherThanThrowing(string stampTemplate)
    {
        string sha = WriteStock();
        WritePatched(stampTemplate.Replace("SHA", sha));

        var selection = PatchStamp.Select(_dir, Warn);

        Assert.Null(selection.Path);
        Assert.False(selection.FfpfscApplied);
        Assert.NotEmpty(_warnings);
    }

    /// <summary>
    /// The regression this consolidates: two readers of the same stamp, only one of them
    /// guarded. There is now a single reader, so a malformed stamp can only ever produce the
    /// resolver's own "ignore it loudly" behaviour — never an exception out of `fpkg version`.
    /// </summary>
    [Fact]
    public void ReadingAMalformedStampNeverThrows()
    {
        string sha = WriteStock();
        WritePatched($"{{\"source\":\"{sha}\",\"patches\":[null]}}");

        var patches = PatchStamp.ReadUnifiedStamp(_dir, Path_(PatchStamp.StockDllName), Warn);

        // [null] parses, and the null element is filtered out rather than crashing.
        Assert.NotNull(patches);
        Assert.Empty(patches!);
    }

    // ---- 6. legacy oodle stamp validation -------------------------------------------------

    [Fact]
    public void AStaleLegacyOodleStampIsRejectedLoudly()
    {
        WriteStock();
        File.WriteAllText(Path_(PatchStamp.LegacyOodleDllName), "legacy");
        File.WriteAllText(Path_(PatchStamp.LegacyOodleStampName),
            $"{{\"sourceSha256\":\"{new string('b', 64)}\"}}");

        var selection = PatchStamp.Select(_dir, Warn);

        Assert.Null(selection.Path);
        Assert.Contains(_warnings, w => w.Contains("native Oodle disabled"));
    }

    [Fact]
    public void AnUnreadableLegacyOodleStampIsRejectedLoudly()
    {
        WriteStock();
        File.WriteAllText(Path_(PatchStamp.LegacyOodleDllName), "legacy");
        File.WriteAllText(Path_(PatchStamp.LegacyOodleStampName), "not json");

        var selection = PatchStamp.Select(_dir, Warn);

        Assert.Null(selection.Path);
        Assert.Contains(_warnings, w => w.Contains("unreadable"));
    }
}

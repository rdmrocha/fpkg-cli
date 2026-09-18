using System.Threading;
using Fpkg.Cli;
using Fpkg.Cli.RepairPlayGo;
using LibProsperoPkg.PKG;
using Xunit;

/// <summary>
/// The acceptance gate for `fpkg repair-playgo`: the whole package, produced through the real
/// CLI entry point, against the 0.6.9 oracle. The region-level tests (Tasks 9 and 10) prove the
/// CNT and the SI are each right; these prove they compose — including the splice of the ~596 MB
/// payload that neither region test touches — and that the user-visible warning is gone.
/// </summary>
public class AcceptanceTests
{
    private static readonly string Passcode = new string('0', 32);

    /// <summary>
    /// Both artifacts are required, and a silently-skipped acceptance gate reports success having
    /// verified nothing — so the message names the missing one rather than shrugging at both.
    /// </summary>
    private static void RequireArtifacts()
    {
        var missing = new List<string>();
        if (!TestPackage.Exists)
            missing.Add($"the test package is not at '{TestPackage.Path}'");
        if (!Oracle.Available)
            missing.Add("the 0.6.9 oracle is not under .oracle/pkg/ — see docs/superpowers/plans/2026-09-18-repair-playgo.md Task 7");
        Skip.If(missing.Count > 0, string.Join("; and ", missing));
    }

    private static string FreshOutPath() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName() + ".pkg");

    /// <summary>
    /// FIXED POINT 2. Repair the 0.6.8 package; the result must be byte-identical to the 0.6.9
    /// rebuild of the same source. This is the gate. Nothing ships without it.
    ///
    /// If it fails, the length assertion localises the fault before the digest does. The whole
    /// 1,360-byte difference between the original (661,006,510) and the oracle (661,005,150) is
    /// the SI's embedded copy of playgo-chunk.dat shrinking from 6,736 to 5,376; the CNT region
    /// stays exactly 0x3CA0000 because body_size does not change, and the outer PFS is untouched.
    /// So a wrong length means the fault is in the SI, and the right length with wrong bytes means
    /// it is in the CNT — or, if Tasks 9 and 10 are still green, in PackageRegions.WriteTo's
    /// splice or in this command's wiring rather than in the repair itself.
    /// </summary>
    [SkippableFact]
    public void TheRepairedPackageEqualsTheOracle()
    {
        RequireArtifacts();
        var outPath = FreshOutPath();
        try
        {
            // Through Run, not the internals: that is what proves the guards, the flag handling
            // and the write path cooperate the way they do for a user typing the command.
            Assert.Equal(0, RepairPlayGoCommand.Run(
                ["repair-playgo", TestPackage.Path, "--out", outPath]));
            Assert.Equal(new FileInfo(Oracle.Path).Length, new FileInfo(outPath).Length);
            Assert.Equal(Bytes.Sha256(Oracle.Path), Bytes.Sha256(outPath));
        }
        finally { File.Delete(outPath); }
    }

    [SkippableFact]
    public void TheRepairedPackagePassesFullVerification()
    {
        RequireArtifacts();
        var outPath = FreshOutPath();
        try
        {
            Assert.Equal(0, RepairPlayGoCommand.Run(
                ["repair-playgo", TestPackage.Path, "--out", outPath]));
            // The full verifier, deliberately: it decodes every block, which is slow on a 661 MB
            // package but is a different and stronger check than the quick pass.
            var result = ProsperoPackageArchive.VerifyPackageFull(
                outPath, Passcode, CancellationToken.None, null, null, 0);
            Assert.Empty(result.Issues);
        }
        finally { File.Delete(outPath); }
    }

    /// <summary>
    /// The user-visible symptom the feature exists to remove: `fpkg verify` warns about the
    /// original package's initial chunk counts and must not warn about the repaired one.
    /// </summary>
    [SkippableFact]
    public void TheRepairClearsThePlayGoWarning()
    {
        RequireArtifacts();
        var outPath = FreshOutPath();
        try
        {
            Assert.NotNull(Program.PlayGoInitialChunkProblem(TestPackage.Path, Passcode));
            Assert.Equal(0, RepairPlayGoCommand.Run(
                ["repair-playgo", TestPackage.Path, "--out", outPath]));
            Assert.Null(Program.PlayGoInitialChunkProblem(outPath, Passcode));
        }
        finally { File.Delete(outPath); }
    }
}

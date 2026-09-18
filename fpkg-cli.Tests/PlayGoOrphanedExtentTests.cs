using System.Buffers.Binary;
using LibProsperoPkg.PlayGo;
using Fpkg.Cli;
using Xunit;

public class PlayGoOrphanedExtentTests
{
    private static readonly string Passcode = new string('0', 32);

    [SkippableFact]
    public void FlagsLanguageChunksThatOwnAnExtentButHoldNoFiles()
    {
        Skip.IfNot(Oracle.Available, "oracle not built");

        var problem = Program.PlayGoOrphanedExtentProblem(Oracle.Path, Passcode);

        Assert.NotNull(problem);
        Assert.Contains("31", problem);
    }

    /// <summary>
    /// The shape Publishing Tools produces: every chunk that owns an extent also owns a file.
    /// The production change that breaks this test is dropping the `populated[k]` guard — without
    /// it the check fires on any package with more than one non-empty chunk, which would make it
    /// useless for telling a repaired package from a broken one.
    /// </summary>
    [Fact]
    public void IgnoresChunksThatOwnAnExtentAndAlsoHoldFiles()
    {
        var chunkDat = ProsperoPlayGo.BuildMultiChunkDat(
            new string('A', 36), [1000, 100], chunkTailSize: 0,
            languageMask: SupportedLanguages, scenarioCount: 1, initialChunkCount: 2,
            defaultLanguageId: 1);

        var problem = Program.PlayGoOrphanedExtentProblem(chunkDat, Ficm([0, 1]));

        Assert.Null(problem);
    }

    /// <summary>All 31 real PlayGo languages; bits 0..32 are unassigned.</summary>
    private const ulong SupportedLanguages = 0xFFFFFFFE00000000;

    /// <summary>A FICM carrying one file per given chunk id: 16-byte header, then 2 bytes each.</summary>
    private static byte[] Ficm(byte[] chunkIds)
    {
        var f = new byte[16 + chunkIds.Length * 2];
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(8), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(12), (uint)(chunkIds.Length * 2));
        for (int i = 0; i < chunkIds.Length; i++) f[16 + i * 2] = chunkIds[i];
        return f;
    }

    /// <summary>
    /// The 0.6.8 fixture has both defects: files spread past the initial chunk set, AND chunks
    /// that own a slice while holding no files. verify --quick must report both, not just the
    /// first one found — during a bisection the set of warnings IS the record of which fixes an
    /// artifact carries, so a check that stops at one makes the record wrong.
    /// </summary>
    [SkippableFact]
    public void ReportsEveryPlayGoWarningNotJustTheFirst()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");

        var warnings = Program.PlayGoWarnings(TestPackage.Path, Passcode);

        Assert.Equal(2, warnings.Count);
    }
}

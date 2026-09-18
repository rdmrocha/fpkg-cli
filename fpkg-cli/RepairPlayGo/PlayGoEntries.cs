using LibProsperoPkg.PlayGo;

namespace Fpkg.Cli.RepairPlayGo;

/// <summary>
/// The three PlayGo files regenerated from a recovered <see cref="PlayGoRecovery"/>:
/// <c>playgo-chunk.dat</c> (entry id 4097), <c>playgo-ficm.dat</c> (entry id 8209) and
/// <c>playgo-scenario.json</c> (entry id 12288).
/// </summary>
internal sealed record PlayGoEntries(byte[] ChunkDat, byte[] Ficm, byte[] ScenarioJson)
{
    /// <summary>FICM's fixed 16-byte header, followed by 2 bytes per file.</summary>
    private const int FicmHeaderSize = 16;
    private const int FicmBytesPerFile = 2;

    internal static PlayGoEntries Build(PlayGoRecovery r, byte[] originalFicm)
    {
        var (main, masks) = Layout(r.DataSize, r.ChunkCount, r.LanguageMask);

        var chunkDat = ProsperoPlayGo.BuildMultiChunkDat(
            r.ContentId, main, r.TailSize,
            publisherNwonly: true, includePublisherLabels: true,
            languageMask: r.LanguageMask,
            scenarioCount: r.ScenarioCount,
            initialChunkCount: r.ChunkCount,
            defaultScenarioId: r.DefaultScenarioId,
            scenarioLabels: r.ScenarioLabels,
            chunkLanguageMasks: masks,
            defaultLanguageId: r.DefaultLanguageId);

        var ficm = ZeroChunkIds(originalFicm);

        var scenarioJson = ProsperoPlayGo.BuildScenarioJson(
            r.ScenarioCount, r.LanguageMask, r.DefaultScenarioId);

        return new PlayGoEntries(chunkDat, ficm, scenarioJson);
    }

    /// <summary>
    /// Returns a modified copy of <paramref name="originalFicm"/> with every chunk id zeroed.
    /// FICM is a 16-byte header followed by 2 bytes per file, the chunk id in the first byte of
    /// each pair; the length is unchanged. The caller's array is never mutated.
    /// </summary>
    private static byte[] ZeroChunkIds(byte[] originalFicm)
    {
        var copy = (byte[])originalFicm.Clone();
        for (int at = FicmHeaderSize; at < copy.Length; at += FicmBytesPerFile)
            copy[at] = 0;
        return copy;
    }

    // BuildLanguageChunkLayout is public but returns the internal nested record
    // ProsperoPlayGo.LanguageChunkLayout, which C# cannot name. This is the only reflection in
    // the feature, and it is over a public method whose return type happens to be internal.
    private static (IReadOnlyList<ulong> Main, IReadOnlyList<ulong> Masks)
        Layout(ulong dataSize, int chunkCount, ulong languageMask)
    {
        var m = typeof(ProsperoPlayGo).Assembly
            .GetType("LibProsperoPkg.PlayGo.ProsperoPlayGo")!
            .GetMethod("BuildLanguageChunkLayout", [typeof(ulong), typeof(int), typeof(ulong)])
            ?? throw new InvalidOperationException(
                "ProsperoPlayGo.BuildLanguageChunkLayout(ulong,int,ulong) not found. The shipped " +
                "LibProsperoPkg has changed; re-derive repair-playgo against it before shipping.");
        var layout = m.Invoke(null, [dataSize, chunkCount, languageMask])
            ?? throw new InvalidOperationException(
                "ProsperoPlayGo.BuildLanguageChunkLayout returned null. The shipped " +
                "LibProsperoPkg has changed; re-derive repair-playgo against it before shipping.");
        var t = layout.GetType();

        var mainProp = t.GetProperty("MainChunkSizes")
            ?? throw new InvalidOperationException(
                "ProsperoPlayGo.LanguageChunkLayout.MainChunkSizes not found. The shipped " +
                "LibProsperoPkg has changed; re-derive repair-playgo against it before shipping.");
        var masksProp = t.GetProperty("ChunkLanguageMasks")
            ?? throw new InvalidOperationException(
                "ProsperoPlayGo.LanguageChunkLayout.ChunkLanguageMasks not found. The shipped " +
                "LibProsperoPkg has changed; re-derive repair-playgo against it before shipping.");

        var main = mainProp.GetValue(layout) as IReadOnlyList<ulong>
            ?? throw new InvalidOperationException(
                "ProsperoPlayGo.LanguageChunkLayout.MainChunkSizes is not an IReadOnlyList<ulong>. " +
                "The shipped LibProsperoPkg has changed; re-derive repair-playgo against it before shipping.");
        var masks = masksProp.GetValue(layout) as IReadOnlyList<ulong>
            ?? throw new InvalidOperationException(
                "ProsperoPlayGo.LanguageChunkLayout.ChunkLanguageMasks is not an IReadOnlyList<ulong>. " +
                "The shipped LibProsperoPkg has changed; re-derive repair-playgo against it before shipping.");

        return (main, masks);
    }
}

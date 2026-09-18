using System.Reflection;
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

    internal static PlayGoEntries Build(PlayGoRecovery r, byte[] originalFicm,
                                       Progress? progress = null)
    {
        var (main, masks) = Layout(r.DataSize, r.ChunkCount, r.LanguageMask);
        progress?.Detail("mainChunkSizes     " + Head(main, v => $"{v:N0}"));
        progress?.Detail("chunkLanguageMasks " + Head(masks, v => $"0x{v:X}"));

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
    /// The first few values of a derived per-chunk list, with a count. These lists are one entry
    /// per chunk — 100 on the test package — so printing them whole would bury the rest of the
    /// verbose output in a single wrapped line.
    /// </summary>
    private static string Head<T>(IReadOnlyList<T> values, Func<T, string> format, int take = 8) =>
        string.Join(", ", values.Take(take).Select(format)) +
        (values.Count > take ? $", … ({values.Count} total)" : "");

    /// <summary>
    /// Returns a modified copy of <paramref name="originalFicm"/> with every chunk id zeroed.
    /// FICM is a 16-byte header followed by 2 bytes per file, the chunk id in the first byte of
    /// each pair; the length is unchanged. The caller's array is never mutated.
    /// </summary>
    private static byte[] ZeroChunkIds(byte[] originalFicm)
    {
        if (originalFicm.Length < FicmHeaderSize)
            throw new InvalidDataException(
                $"playgo-ficm.dat is too small: {originalFicm.Length} bytes, need at least " +
                $"{FicmHeaderSize} for its header.");
        if ((originalFicm.Length - FicmHeaderSize) % FicmBytesPerFile != 0)
            throw new InvalidDataException(
                $"playgo-ficm.dat's file table ({originalFicm.Length - FicmHeaderSize} bytes after " +
                $"the header) is not a whole number of {FicmBytesPerFile}-byte per-file entries.");

        var copy = (byte[])originalFicm.Clone();
        for (int at = FicmHeaderSize; at < copy.Length; at += FicmBytesPerFile)
            copy[at] = 0;
        return copy;
    }

    /// <summary>
    /// BuildLanguageChunkLayout is public but returns the internal nested record
    /// ProsperoPlayGo.LanguageChunkLayout, which C# cannot name. This is the only reflection in
    /// the feature, and it is over a public method whose return type happens to be internal.
    /// Every shape assumption below is checked explicitly and fails with a message telling a
    /// future maintainer what to actually do, rather than a bare reflection exception.
    /// </summary>
    private static (IReadOnlyList<ulong> Main, IReadOnlyList<ulong> Masks)
        Layout(ulong dataSize, int chunkCount, ulong languageMask)
    {
        // ProsperoPlayGo is itself a compile-time-visible public type, so it is named directly —
        // no string-based Assembly.GetType lookup. If it ever disappeared upstream, this file
        // would fail to compile, which is a better failure than any runtime check could give.
        var method = typeof(ProsperoPlayGo).GetMethod(
            "BuildLanguageChunkLayout", [typeof(ulong), typeof(int), typeof(ulong)]);
        Expect(method is { IsStatic: true },
            "ProsperoPlayGo.BuildLanguageChunkLayout(ulong,int,ulong) exists as a public static method");

        var layout = method!.Invoke(null, [dataSize, chunkCount, languageMask]);
        Expect(layout is not null, "BuildLanguageChunkLayout returned a non-null LanguageChunkLayout");

        var type = layout!.GetType();
        var mainProp = type.GetProperty("MainChunkSizes");
        Expect(IsInstanceReadOnlyListOfUlong(mainProp),
            "LanguageChunkLayout.MainChunkSizes exists as an instance IReadOnlyList<ulong> property");

        var masksProp = type.GetProperty("ChunkLanguageMasks");
        Expect(IsInstanceReadOnlyListOfUlong(masksProp),
            "LanguageChunkLayout.ChunkLanguageMasks exists as an instance IReadOnlyList<ulong> property");

        var main = (IReadOnlyList<ulong>)mainProp!.GetValue(layout)!;
        var masks = (IReadOnlyList<ulong>)masksProp!.GetValue(layout)!;
        return (main, masks);
    }

    private static bool IsInstanceReadOnlyListOfUlong(PropertyInfo? property) =>
        property is { PropertyType: var t } &&
        t == typeof(IReadOnlyList<ulong>) &&
        property.GetMethod is { IsStatic: false };

    private static void Expect(bool ok, string what)
    {
        if (!ok)
            throw new InvalidOperationException(
                $"shape check failed: {what}. The shipped LibProsperoPkg has changed; re-derive " +
                "repair-playgo against it before shipping.");
    }
}

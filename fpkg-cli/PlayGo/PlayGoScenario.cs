using System.Text;
using LibProsperoPkg.PlayGo;

namespace Fpkg.Cli.PlayGo;

/// <summary>
/// Generates <c>playgo-scenario.json</c> in the form the Windows toolkit ships.
///
/// <para><b>Why not the library's.</b> LibProsperoPkg's
/// <c>ProsperoPlayGo.BuildScenarioJson</c> emits 2,293 bytes; CNT entry 12288 in BOTH Windows-built
/// oracles is 3,248 bytes, and the two titles' copies are byte-identical
/// (<c>ad885f5f064efcbb…</c>). So it is not derived from the title at all — it is a canonical
/// document the folder-to-GP5 wrapper supplies, and the difference is that Sony's carries a
/// per-language <c>title</c>/<c>description</c> block for every supported language.</para>
///
/// <para><b>Measured shape</b>, from that file: CRLF line endings including a trailing one, two
/// spaces of indent per level, keys in the order below, the scenario typed <c>playmode</c>, and
/// every language block identical — <c>"Scenario #N"</c> for both title and description. The
/// languages appear in PlayGo index order (0 ja-JP, 1 en-US, … 30 uk-UA), which is the order
/// <see cref="ProsperoPlayGoLanguages"/> already publishes.</para>
/// </summary>
internal static class PlayGoScenario
{
    /// <summary>The sha256 of the oracle's document, for the test that pins this generator.</summary>
    internal const string OracleSha256 =
        "ad885f5f064efcbb5cd37c57234104cfb920ab5132b3e7db310075b0f2ca9260";

    /// <summary>
    /// The document for <paramref name="scenarioCount"/> scenarios over
    /// <paramref name="languages"/>, defaulting to <paramref name="defaultLanguage"/>.
    /// </summary>
    internal static byte[] Build(int scenarioCount, int defaultScenarioId, string defaultLanguage,
                                 IReadOnlyList<string> languages)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(scenarioCount, 1);
        var sb = new StringBuilder();
        // CRLF everywhere, including after the closing brace: measured on the oracle, where 135
        // LF are all preceded by CR and the file ends "}\r\n".
        void Line(string text) => sb.Append(text).Append("\r\n");

        Line("{");
        Line($"  \"scenarioCount\": {scenarioCount},");
        Line($"  \"scenarioDefaultId\": {defaultScenarioId},");
        Line($"  \"scenarioDefaultLanguage\": \"{defaultLanguage}\",");
        Line("  \"scenarios\": [");
        for (int id = 0; id < scenarioCount; id++)
        {
            Line("    {");
            Line($"      \"id\": {id},");
            Line("      \"type\": \"playmode\",");
            for (int i = 0; i < languages.Count; i++)
            {
                Line($"      \"{languages[i]}\": {{");
                Line($"        \"title\": \"Scenario #{id}\",");
                Line($"        \"description\": \"Scenario #{id}\"");
                Line("      }" + (i == languages.Count - 1 ? "" : ","));
            }
            Line("    }" + (id == scenarioCount - 1 ? "" : ","));
        }
        Line("  ]");
        Line("}");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// The language codes in PlayGo index order, as the library publishes them.
    /// </summary>
    internal static IReadOnlyList<string> AllLanguages() =>
        [.. ProsperoPlayGoLanguages.Supported.Select(l => l.Code)];
}

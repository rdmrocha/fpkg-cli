using System.Security.Cryptography;
using Fpkg.Cli.PlayGo;
using Xunit;

/// <summary>
/// The generator is pinned to the oracle by hash. If it ever stops producing those exact bytes the
/// package stops matching the Windows toolkit's CNT entry 12288, which is the only reason this
/// code exists rather than the library's own BuildScenarioJson.
/// </summary>
public class PlayGoScenarioTests
{
    [Fact]
    public void ReproducesTheWindowsOracleByteForByte()
    {
        byte[] doc = PlayGoScenario.Build(1, 0, "en-US", PlayGoScenario.AllLanguages());
        Assert.Equal(3248, doc.Length);
        Assert.Equal(PlayGoScenario.OracleSha256,
                     Convert.ToHexString(SHA256.HashData(doc)).ToLowerInvariant());
    }

    [Fact]
    public void UsesCrlfThroughoutIncludingTheLastLine()
    {
        byte[] doc = PlayGoScenario.Build(1, 0, "en-US", PlayGoScenario.AllLanguages());
        string text = System.Text.Encoding.UTF8.GetString(doc);
        Assert.EndsWith("}\r\n", text, StringComparison.Ordinal);
        Assert.Equal(text.Split('\n').Length - 1, text.Split("\r\n").Length - 1);
    }

    [Fact]
    public void IsValidJsonWithTheMeasuredShape()
    {
        using var j = System.Text.Json.JsonDocument.Parse(
            PlayGoScenario.Build(2, 0, "en-US", ["ja-JP", "en-US"]));
        var root = j.RootElement;
        Assert.Equal(2, root.GetProperty("scenarioCount").GetInt32());
        var scenarios = root.GetProperty("scenarios");
        Assert.Equal(2, scenarios.GetArrayLength());
        Assert.Equal("playmode", scenarios[1].GetProperty("type").GetString());
        Assert.Equal("Scenario #1", scenarios[1].GetProperty("ja-JP").GetProperty("title").GetString());
    }

    [Fact]
    public void AllThirtyOneLanguagesAppearInPlayGoIndexOrder()
    {
        var langs = PlayGoScenario.AllLanguages();
        Assert.Equal(31, langs.Count);
        Assert.Equal("ja-JP", langs[0]);
        Assert.Equal("en-US", langs[1]);
        Assert.Equal("uk-UA", langs[30]);
    }
}

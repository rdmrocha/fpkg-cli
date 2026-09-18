using System.Text.RegularExpressions;
using Fpkg.Cli.RepairPlayGo;
using Xunit;

public class ProgressTests
{
    [Fact]
    public void StagesAreNumberedAndClosedWithElapsedTime()
    {
        var sw = new StringWriter();
        var p = new Progress(sw, verbose: false, isTty: false, totalStages: 2);
        p.Stage("reading package");
        p.Stage("resealing");
        p.Finish();

        string s = sw.ToString();
        Assert.Contains("[1/2] reading package", s);
        Assert.Contains("[2/2] resealing", s);
        Assert.Equal(2, Regex.Matches(s, @"\d+(\.\d+)?\s*(ms|s)\b").Count);
    }

    [Fact]
    public void DetailIsSuppressedUnlessVerbose()
    {
        var quiet = new StringWriter();
        new Progress(quiet, verbose: false, isTty: false, totalStages: 1).Detail("inner value 42");
        Assert.DoesNotContain("inner value 42", quiet.ToString());

        var loud = new StringWriter();
        new Progress(loud, verbose: true, isTty: false, totalStages: 1).Detail("inner value 42");
        Assert.Contains("inner value 42", loud.ToString());
    }

    [Fact]
    public void PercentagesAreThrottledAndMonotonic()
    {
        var sw = new StringWriter();
        var p = new Progress(sw, verbose: false, isTty: false, totalStages: 1);
        p.Stage("writing");
        for (long i = 0; i <= 1000; i++) p.Report(i, 1000);
        p.Finish();

        var pcts = Regex.Matches(sw.ToString(), @"(\d+)%").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.NotEmpty(pcts);
        Assert.True(pcts.Count <= 21, $"expected at most 21 emissions at 5% granularity, got {pcts.Count}");
        Assert.Equal(pcts.OrderBy(x => x), pcts);            // monotonic
        Assert.Equal(pcts.Distinct(), pcts);                 // no duplicates
        Assert.DoesNotContain(0, pcts);
    }

    [Fact]
    public void ReportBeforeAnyStageDoesNotThrow()
    {
        var sw = new StringWriter();
        var p = new Progress(sw, verbose: false, isTty: false, totalStages: 1);
        p.Report(1, 2);      // must be a no-op, not a crash
        Assert.Equal("", sw.ToString());
        p.Finish();
        Assert.Equal("", sw.ToString());
    }
}

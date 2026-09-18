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
        // quietBelow: Zero — this test is about the 250 ms / 5-point throttle, not about the
        // separate rule that hides percentages for a stage that finishes in under 200 ms. With the
        // default the whole loop would run inside that window and print nothing at all.
        var p = new Progress(sw, verbose: false, isTty: false, totalStages: 1, quietBelow: TimeSpan.Zero);
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

    /// <summary>
    /// A stage that showed a percentage must end on exactly 100. The throttle suppresses the last
    /// step — which is usually a small one — so left to it every stage stopped at 98 or 99% and the
    /// line lingered there at that value. Note the loop below deliberately never reports 100/100.
    /// </summary>
    [Fact]
    public void AStageThatReportedProgressEndsAtExactlyOneHundred()
    {
        var sw = new StringWriter();
        var p = new Progress(sw, verbose: false, isTty: false, totalStages: 1, quietBelow: TimeSpan.Zero);
        p.Stage("writing");
        for (long i = 0; i <= 993; i++) p.Report(i, 1000);
        p.Finish();

        var pcts = Regex.Matches(sw.ToString(), @"(\d+)%").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.Equal(100, pcts[^1]);
        Assert.Single(pcts, x => x == 100);                  // emitted once, not twice
        // And the 100% comes before the elapsed time, not after it.
        Assert.True(sw.ToString().IndexOf("100%", StringComparison.Ordinal)
                  < sw.ToString().IndexOf("done in", StringComparison.Ordinal));
    }

    /// <summary>
    /// A stage that finishes inside quietBelow shows no percentage at all — not even the terminal
    /// 100%. Flashing a single number for a stage that is over before it is read is noise.
    /// </summary>
    [Fact]
    public void AStageFasterThanQuietBelowShowsNoPercentage()
    {
        var sw = new StringWriter();
        var p = new Progress(sw, verbose: false, isTty: false, totalStages: 1,
                             quietBelow: TimeSpan.FromMinutes(5));
        p.Stage("reading package");
        for (long i = 0; i <= 1000; i++) p.Report(i, 1000);
        p.Finish();

        string s = sw.ToString();
        Assert.DoesNotContain("%", s);
        Assert.Contains("[1/1] reading package", s);
        Assert.Contains("done in", s);
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

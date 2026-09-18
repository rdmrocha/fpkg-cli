using System.Diagnostics;

namespace Fpkg.Cli.RepairPlayGo;

/// <summary>
/// Staged, throttled progress reporting for <c>repair-playgo</c>. Prints a numbered
/// <c>[n/total] name</c> line when each stage opens, an occasional percentage line while it
/// runs, and an elapsed time when it closes.
///
/// <para>
/// <see cref="Report"/> is designed to be called very often — per 80 KiB block in some later
/// stages (resealing a CNT, a CRC pass over the payload, writing a journal) — so it must be
/// cheap and heavily throttled: at most every 250 ms or every 5 percentage points, whichever is
/// sooner, and never for 0% or a repeated percentage.
/// </para>
/// </summary>
internal sealed class Progress
{
    private readonly TextWriter _output;
    private readonly bool _verbose;
    private readonly bool _isTty;
    private readonly int _totalStages;

    private readonly Stopwatch _stageClock = new();
    private int _stageIndex;
    private string? _stageName;
    private bool _lineOpen;

    private long _lastEmitTicks;
    private int _lastPercent;

    internal Progress(TextWriter output, bool verbose, bool isTty, int totalStages)
    {
        _output = output;
        _verbose = verbose;
        _isTty = isTty;
        _totalStages = totalStages;
    }

    /// <summary>Starts a stage. Closes the previous one with its elapsed time.</summary>
    internal void Stage(string name)
    {
        CloseCurrentStage();

        _stageIndex++;
        _stageName = name;
        _lastEmitTicks = -1; // sentinel: nothing emitted yet this stage (0 is a real tick value)
        _lastPercent = 0;
        _stageClock.Restart();

        _output.WriteLine($"[{_stageIndex}/{_totalStages}] {name}");
    }

    /// <summary>Throttled percentage within the current stage. Safe to call per 80 KiB block.</summary>
    internal void Report(long done, long total)
    {
        if (_stageName is null || total <= 0)
            return;

        int pct = (int)(done * 100 / total);
        // Clamp to a valid percentage, and never let it move backwards within a stage — callers
        // (later tasks forward library callbacks directly) are not trusted to be well-behaved.
        if (pct < 0) pct = 0;
        if (pct > 100) pct = 100;
        if (pct < _lastPercent) pct = _lastPercent;
        if (pct <= 0 || pct == _lastPercent)
            return;

        long nowTicks = _stageClock.ElapsedTicks;
        long elapsedSinceEmitMs = (nowTicks - _lastEmitTicks) * 1000 / Stopwatch.Frequency;
        bool timeElapsed = _lastEmitTicks < 0 || elapsedSinceEmitMs >= 250;
        bool percentJumped = pct - _lastPercent >= 5;
        if (!timeElapsed && !percentJumped)
            return;

        _lastEmitTicks = nowTicks;
        _lastPercent = pct;

        if (_isTty)
        {
            _output.Write($"\r  {_stageName} {pct,3}%");
            _lineOpen = true;
        }
        else
        {
            _output.WriteLine($"  {_stageName} {pct}%");
        }
    }

    /// <summary>Detail printed only under --verbose.</summary>
    internal void Detail(string line)
    {
        if (!_verbose)
            return;

        _output.WriteLine($"  {line}");
    }

    /// <summary>Closes the final stage.</summary>
    internal void Finish() => CloseCurrentStage();

    private void CloseCurrentStage()
    {
        if (_stageName is null)
            return;

        if (_isTty && _lineOpen)
        {
            _output.Write("\r" + new string(' ', _stageName.Length + 8) + "\r");
            _lineOpen = false;
        }

        _stageClock.Stop();
        _output.WriteLine($"  {_stageName} done in {FormatElapsed(_stageClock.Elapsed)}");

        _stageName = null;
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalSeconds >= 1
            ? $"{elapsed.TotalSeconds:0.0}s"
            : $"{elapsed.TotalMilliseconds:0}ms";
}

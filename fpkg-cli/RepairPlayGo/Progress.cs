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
/// sooner, and never for 0% or a repeated percentage. A stage that finishes inside
/// <c>quietBelow</c> shows no percentage at all; one that showed any ends at exactly 100%.
/// </para>
/// </summary>
internal sealed class Progress
{
    private readonly TextWriter _output;
    private readonly bool _verbose;
    private readonly bool _isTty;
    private readonly int _totalStages;
    private readonly long _quietBelowMs;

    private readonly Stopwatch _stageClock = new();
    private int _stageIndex;
    private string? _stageName;
    private bool _lineOpen;

    private long _lastEmitTicks;
    private int _lastPercent;

    /// <param name="quietBelow">
    /// A stage that finishes faster than this never shows a percentage at all — flashing one number
    /// and vanishing is noise, not progress. The stage line and its elapsed time are still printed.
    /// Tests pass <see cref="TimeSpan.Zero"/> to exercise the throttle itself without sleeping.
    /// </param>
    internal Progress(TextWriter output, bool verbose, bool isTty, int totalStages,
                      TimeSpan? quietBelow = null)
    {
        _output = output;
        _verbose = verbose;
        _isTty = isTty;
        _totalStages = totalStages;
        _quietBelowMs = (long)(quietBelow ?? TimeSpan.FromMilliseconds(200)).TotalMilliseconds;
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
        if (_lastEmitTicks < 0)
        {
            // Nothing emitted yet this stage. Hold everything back until the stage has been running
            // long enough to be worth a percentage at all; if it finishes first, it stays quiet.
            if (nowTicks * 1000 / Stopwatch.Frequency < _quietBelowMs)
                return;
        }
        else
        {
            long elapsedSinceEmitMs = (nowTicks - _lastEmitTicks) * 1000 / Stopwatch.Frequency;
            bool percentJumped = pct - _lastPercent >= 5;
            if (elapsedSinceEmitMs < 250 && !percentJumped)
                return;
        }

        Emit(pct, nowTicks);
    }

    private void Emit(int pct, long atTicks)
    {
        _lastEmitTicks = atTicks;
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

        // On a TTY the percentage line is left open (written with \r and no newline), so a detail
        // line written straight out would print ON TOP of it and leave the percentage's tail
        // trailing past the end of the shorter of the two. Erase it first; the next Report reopens
        // one.
        ClearOpenLine();
        _output.WriteLine($"  {line}");
    }

    /// <summary>Closes the final stage.</summary>
    internal void Finish() => CloseCurrentStage();

    private void CloseCurrentStage()
    {
        if (_stageName is null)
            return;

        // A stage that reported progress ends AT 100%, always. The throttle exists to suppress the
        // stream of intermediate values, and the last step is usually a small one, so left to the
        // throttle every stage would stop at 98 or 99% and linger there. This one emission bypasses
        // it. A stage that stayed quiet — no progress at all, or one that finished inside
        // quietBelow — stays quiet: it gets its elapsed time and nothing else.
        if (_lastEmitTicks >= 0 && _lastPercent < 100)
            Emit(100, _stageClock.ElapsedTicks);

        ClearOpenLine();
        _stageClock.Stop();
        _output.WriteLine($"  {_stageName} done in {FormatElapsed(_stageClock.Elapsed)}");

        _stageName = null;
    }

    /// <summary>
    /// Erases the open <c>\r … n%</c> line, if there is one. No-op when not on a TTY (every line is
    /// already newline-terminated there) or when nothing is open.
    /// </summary>
    private void ClearOpenLine()
    {
        if (!_isTty || !_lineOpen || _stageName is null)
            return;

        _output.Write("\r" + new string(' ', _stageName.Length + 8) + "\r");
        _lineOpen = false;
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalSeconds >= 1
            ? $"{elapsed.TotalSeconds:0.0}s"
            : $"{elapsed.TotalMilliseconds:0}ms";
}

/// <summary>
/// A pass-through read wrapper that reports the underlying stream's position as a percentage of
/// its length — the only progress signal available for a library call that reads a whole stream
/// front to back and offers no callback of its own (<c>ProsperoPlayGo.BuildChunkCrc</c>).
///
/// <para>
/// Only for a call that really does consume the stream end to end. A call that SEEKS to a range
/// near the end of a large file reports ~99% before it has read a useful byte, and the monotonic
/// clamp in <see cref="Progress.Report"/> then pins it there — such a caller must count the bytes
/// it actually reads instead (see <c>PackageRegions.Load</c>).
/// </para>
///
/// <para>
/// PURELY OBSERVATIONAL. Every member delegates to the inner stream, nothing is buffered,
/// re-chunked or re-ordered, and <see cref="Dispose"/> does NOT dispose the inner stream — the
/// caller's own <c>using</c> owns that. Writes and <see cref="SetLength"/> throw rather than
/// silently succeeding: this is a read-side wrapper and a caller that writes through it has made
/// a mistake worth hearing about.
/// </para>
/// </summary>
internal sealed class ReadingProgressStream(Stream inner, Progress progress) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = inner.Read(buffer, offset, count);
        progress.Report(inner.Position, inner.Length);
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        int n = inner.Read(buffer);
        progress.Report(inner.Position, inner.Length);
        return n;
    }

    public override int ReadByte()
    {
        int b = inner.ReadByte();
        progress.Report(inner.Position, inner.Length);
        return b;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() => inner.Flush();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

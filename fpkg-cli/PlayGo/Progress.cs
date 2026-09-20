namespace Fpkg.Cli.PlayGo;

/// <summary>
/// Progress for the long stages of <c>playgo-fix</c>.
/// <para>
/// On the reference fixture every stage is sub-second and this never matters. On an 87 GB package it very
/// much does: measuring 217 inner files and re-digesting a 194 MB CNT take long enough that a
/// silent command is indistinguishable from a hung one. A stuck-looking build has already been
/// reported once on this project, so the stages announce themselves and the counter refreshes.
/// </para>
/// Output goes to stderr so a redirected stdout stays clean. On a non-TTY the bar is suppressed
/// and only the stage lines survive, which keeps build logs readable.
/// </summary>
internal sealed class Progress(Action<string>? detail = null, TextWriter? output = null, bool? isTty = null)
{
    private readonly TextWriter _out = output ?? Console.Error;
    private readonly bool _tty = isTty ?? !Console.IsErrorRedirected;
    private string _stage = "";
    private long _last = -1;
    private DateTime _next = DateTime.MinValue;

    /// <summary>Names the stage now running and resets the counter.</summary>
    internal void Stage(string name)
    {
        Close();
        _stage = name;
        _last = -1;
        _next = DateTime.MinValue;
        _out.WriteLine($"  {name}…");
        _out.Flush();
    }

    /// <summary>
    /// Refreshes the counter. Throttled to ~10/s and to whole percent, so a tight loop over
    /// hundreds of thousands of blocks does not spend its time formatting strings.
    /// </summary>
    internal void Report(long done, long total)
    {
        if (!_tty || total <= 0 || _stage.Length == 0) return;
        long pct = done * 100 / total;
        var now = DateTime.UtcNow;
        if (pct == _last && now < _next) return;
        _last = pct;
        _next = now.AddMilliseconds(100);
        _out.Write($"\r  {_stage}… {pct,3}%  ({done:N0}/{total:N0})   ");
        _out.Flush();
    }

    internal void Detail(string line) => detail?.Invoke(line);

    /// <summary>Ends the current stage, clearing the counter line if one was drawn.</summary>
    internal void Finish() => Close();

    private void Close()
    {
        if (_tty && _last >= 0) { _out.Write('\r' + new string(' ', 72) + '\r'); _out.Flush(); }
        _last = -1;
    }
}

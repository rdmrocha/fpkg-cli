namespace Fpkg.Cli;

/// <summary>
/// Progress lines for <c>fpkg extract</c>, built on the library's two callbacks: overall percent
/// (by bytes) and "this file is done". The library extracts inner files on up to eight threads
/// and only reports a file once it is written, so there is no single "current" file. Each line
/// names a file that has just been written, plus the overall percent at that moment. A bare
/// percent line also appears when the percent moves without a file finishing, so a single
/// multi-gigabyte file still shows movement. Both callbacks come from worker threads, so
/// everything goes through one lock.
/// </summary>
internal sealed class ExtractProgress(string phase)
{
    private readonly object sync = new();
    private int percent;
    private int printedPercent = -1;
    private int files;

    internal int Files => files;

    internal void Percent(int value)
    {
        lock (sync)
        {
            if (value <= percent && printedPercent >= 0) return;
            percent = Math.Max(percent, value);
            if (percent == printedPercent) return;
            printedPercent = percent;
            Console.WriteLine($"  [{percent,3}%] {phase}");
        }
    }

    internal void File(string relativePath)
    {
        lock (sync)
        {
            files++;
            printedPercent = percent;
            Console.WriteLine($"  [{percent,3}%] {relativePath}");
        }
    }
}

using System.Diagnostics;
using XTerm.Options;

namespace XTerm.Bench;

/// <summary>
/// Attributes flood's remaining allocation, rather than inferring it from reading code.
///
/// flood is "y\r\n" repeated: it barely prints, so its cost is the newline path. Isolating the
/// pieces -- print only, CR+LF only, recycling on versus off -- says which one allocates, which
/// guessing from the source had already got wrong once.
/// </summary>
public static class FloodProbe
{
    public static void Run()
    {
        Console.WriteLine($"{"variant",-34} {"bytes/line",12} {"ns/line",10}");
        Console.WriteLine(new string('-', 60));

        Measure("y only (no newline)",        "y",       true);
        Measure("y\\r\\n  recycling ON",        "y\r\n",   true);
        Measure("y\\r\\n  recycling OFF",       "y\r\n",   false);
        Measure("\\n only  recycling ON",       "\n",      true);
        Measure("\\r only (no scroll)",         "\r",      true);
    }

    private static void Measure(string label, string unit, bool recycle)
    {
        var terminal = new Terminal(new TerminalOptions { Cols = 240, Rows = 67 });
        terminal.Buffer.RecycleScrolledLines = recycle;

        // One chunk of many repetitions, so per-Write overhead is not what gets measured.
        const int perChunk = 4096;
        var chunk = string.Concat(Enumerable.Repeat(unit, perChunk));

        for (var warm = 0; warm < 40; warm++)
            terminal.Write(chunk);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        const int chunks = 200;
        for (var i = 0; i < chunks; i++)
            terminal.Write(chunk);
        sw.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        long units = (long)chunks * perChunk;
        Console.WriteLine($"{label,-34} {(double)allocated / units,12:N2} {sw.Elapsed.TotalMilliseconds * 1_000_000.0 / units,10:N1}");
    }
}

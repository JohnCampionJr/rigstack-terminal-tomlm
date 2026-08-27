using System.Diagnostics;
using System.Text;
using XTerm.Options;

namespace XTerm.Bench;

/// <summary>
/// String entry against byte entry, on the same corpus.
///
/// The byte entry exists to delete work the string entry forces: a UTF-16 transcode per read, plus a
/// per-character walk where a vectorised scan would do. This measures whether that is true, including
/// the transcode — a caller holding PTY bytes has to pay Encoding.UTF8.GetString to use the string
/// overload at all, so charging it here is the honest comparison.
/// </summary>
public static class ByteEntryProbe
{
    public static void Run(double seconds)
    {
        Console.WriteLine($"{"corpus",-14} {"string MiB/s",13} {"bytes MiB/s",12} {"speedup",9} {"str B/char",11} {"byte B/char",12}");
        Console.WriteLine(new string('-', 78));

        foreach (var spec in CorpusGenerator.Specs)
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "corpus");
            CorpusGenerator.GenerateAll(dir, 400_000, 240, 67);
            var text = File.ReadAllText(Path.Combine(dir, spec.Name + ".vt"));

            var byteChunks = new List<byte[]>();
            var stringChunks = new List<string>();
            for (var i = 0; i < text.Length; i += 4096)
            {
                var slice = text.Substring(i, Math.Min(4096, text.Length - i));
                stringChunks.Add(slice);
                byteChunks.Add(Encoding.UTF8.GetBytes(slice));
            }

            var totalBytes = byteChunks.Sum(b => (long)b.Length);

            // Transcode INSIDE the timed loop. A caller holding PTY bytes cannot use the string
            // overload without doing this, so leaving it out would credit the string path with work
            // it does not get to skip.
            var (strRate, strAlloc) = Time(seconds, totalBytes, t =>
            {
                foreach (var c in byteChunks) t.Write(Encoding.UTF8.GetString(c));
            });

            // Kept only to prove the two chunkings carry the same content.
            _ = stringChunks;

            // The byte path is fed the bytes directly, as a PTY hands them over.
            var (byteRate, byteAlloc) = Time(seconds, totalBytes, t =>
            {
                foreach (var c in byteChunks) t.Write(c.AsSpan());
            });

            Console.WriteLine($"{spec.Name,-14} {strRate,13:N1} {byteRate,12:N1} {byteRate / strRate,8:N2}x {strAlloc,11:N2} {byteAlloc,12:N2}");
        }
    }

    private static (double MiBPerSec, double BytesPerChar) Time(double seconds, long bytesPerPass, Action<Terminal> pass)
    {
        var terminal = new Terminal(new TerminalOptions { Cols = 240, Rows = 67 });

        var previous = double.MaxValue;
        for (var warm = 0; warm < 12; warm++)
        {
            var w = Stopwatch.StartNew();
            pass(terminal);
            w.Stop();
            var now = w.Elapsed.TotalMilliseconds;
            if (previous < double.MaxValue && Math.Abs(now - previous) / previous < 0.05) break;
            previous = now;
        }

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        long done = 0;
        while (sw.Elapsed.TotalSeconds < seconds) { pass(terminal); done += bytesPerPass; }
        sw.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        return (done / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds, (double)allocated / done);
    }
}

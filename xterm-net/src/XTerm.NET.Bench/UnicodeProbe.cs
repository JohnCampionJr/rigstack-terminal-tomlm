using System.Diagnostics;
using XTerm.Options;

namespace XTerm.Bench;

/// <summary>
/// Attributes what the unicode corpus still allocates, by content class.
///
/// The corpus mixes CJK, kana, ZWJ emoji, regional indicators, combining marks and ASCII, so its
/// aggregate figure says nothing about which of them is responsible. Each class is fed on its own
/// here. Two suspects going in: codepoints above the BMP miss the string cache, and the combining
/// path builds strings to merge into the previous cell.
/// </summary>
public static class UnicodeProbe
{
    public static void Run()
    {
        Console.WriteLine($"{"content",-38} {"bytes/char",12} {"ns/char",10}");
        Console.WriteLine(new string('-', 64));

        Measure("ASCII",                       "abcdefghij");
        Measure("CJK (BMP)",                   "世界中文字体");
        Measure("kana (BMP)",                  "こんにちはあ");
        Measure("box drawing (BMP)",           "│━┃─┏┓┗┛");
        Measure("emoji, single (above BMP)",   "😀😁😂🤣😃😄");
        Measure("emoji ZWJ sequence",          "👨‍👩‍👧‍👦");
        Measure("regional indicators",         "🇯🇵🇺🇸🇬🇧");
        Measure("combining marks",             "éàôüñç");
    }

    private static void Measure(string label, string unit)
    {
        var terminal = new Terminal(new TerminalOptions { Cols = 240, Rows = 67 });

        var chunk = string.Concat(Enumerable.Repeat(unit + "\r\n", 256));

        for (var warm = 0; warm < 40; warm++)
            terminal.Write(chunk);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        const int chunks = 200;
        for (var i = 0; i < chunks; i++)
            terminal.Write(chunk);
        sw.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        long chars = (long)chunks * chunk.Length;
        Console.WriteLine($"{label,-38} {(double)allocated / chars,12:N2} {sw.Elapsed.TotalMilliseconds * 1_000_000.0 / chars,10:N1}");
    }
}

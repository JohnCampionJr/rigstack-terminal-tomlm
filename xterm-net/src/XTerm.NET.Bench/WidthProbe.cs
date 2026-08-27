using System.Diagnostics;
using System.Text;
using Wcwidth;

namespace XTerm.Bench;

/// <summary>
/// Sizes the width-lookup win before committing to it.
///
/// Ghostty replaced its off-the-shelf width library with a custom table and reported 2.8x on ASCII
/// throughput. XTerm.NET calls Wcwidth's UnicodeCalculator.GetWidth once per rune. Before rewriting
/// anything, this measures the library call against a direct-indexed table on the same codepoints,
/// so the decision rests on a number rather than on the analogy holding.
/// </summary>
public static class WidthProbe
{
    public static void Run(int millionsOfLookups)
    {
        // A codepoint mix resembling the unicode corpus: ASCII, CJK, kana, box drawing, combining.
        var points = new List<int>();
        for (var c = 0x20; c < 0x7F; c++) points.Add(c);
        for (var c = 0x4E00; c < 0x4E80; c++) points.Add(c);   // CJK
        for (var c = 0x3040; c < 0x30A0; c++) points.Add(c);   // kana
        for (var c = 0x2500; c < 0x2580; c++) points.Add(c);   // box drawing
        for (var c = 0x0300; c < 0x0330; c++) points.Add(c);   // combining marks
        var arr = points.ToArray();

        var iterations = millionsOfLookups * 1_000_000 / arr.Length;

        // --- library ---
        long sink = 0;
        var sw = Stopwatch.StartNew();
        for (var it = 0; it < iterations; it++)
            foreach (var cp in arr)
                sink += UnicodeCalculator.GetWidth(new Rune(cp));
        sw.Stop();
        var libNs = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / (iterations * (long)arr.Length);

        // --- table, built from the same library so results are identical by construction ---
        var table = new sbyte[0x10000];
        for (var cp = 0; cp < 0x10000; cp++)
            table[cp] = char.IsSurrogate((char)cp) ? (sbyte)-1 : (sbyte)UnicodeCalculator.GetWidth(new Rune(cp));

        long sink2 = 0;
        var sw2 = Stopwatch.StartNew();
        for (var it = 0; it < iterations; it++)
            foreach (var cp in arr)
                sink2 += table[cp];
        sw2.Stop();
        var tableNs = sw2.Elapsed.TotalMilliseconds * 1_000_000.0 / (iterations * (long)arr.Length);

        Console.WriteLine($"lookups      : {iterations * (long)arr.Length:N0}  (checksums {sink} / {sink2} — must match)");
        Console.WriteLine($"Wcwidth call : {libNs,7:N2} ns/lookup");
        Console.WriteLine($"table index  : {tableNs,7:N2} ns/lookup");
        Console.WriteLine($"speedup      : {libNs / tableNs,7:N1}x   (table costs {0x10000 / 1024} KB)");
    }
}

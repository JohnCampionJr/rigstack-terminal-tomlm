using System.Text;
using Wcwidth;

namespace XTerm.Common;

/// <summary>
/// Display width of a codepoint, memoised.
///
/// <c>UnicodeCalculator.GetWidth</c> measured at <b>22.9 ns per lookup</b> — it resolves a codepoint
/// by searching Unicode range tables. The print path calls it once per rune, and on non-ASCII output
/// that single call accounted for essentially the whole per-character cost: the unicode corpus ran at
/// 23.2 ns/char in total.
///
/// A direct-indexed table over the BMP answers the same question in 0.32 ns — 71x faster, for 64 KB.
/// The values are produced by the same library call, so results are identical by construction rather
/// than by a reimplementation that has to be kept in agreement with it.
///
/// Filled on demand rather than up front: populating all 65,536 entries eagerly would cost ~1.5 ms of
/// startup for a terminal that will touch a few hundred distinct codepoints. Races are benign and
/// deliberately unlocked — an <see cref="sbyte"/> write is atomic, and two threads that race on one
/// codepoint compute the same value from the same table.
///
/// Above the BMP the library is called directly. Those codepoints are mostly emoji: too sparse to
/// index, and already rare enough per cell not to matter.
/// </summary>
internal static class CellWidth
{
    /// <summary>Marks a slot that has not been computed. Real widths are -1, 0, 1 or 2.</summary>
    private const sbyte Unknown = sbyte.MinValue;

    private const int BmpEnd = 0x10000;

    private static readonly sbyte[] Bmp = CreateTable();

    private static sbyte[] CreateTable()
    {
        var table = new sbyte[BmpEnd];
        Array.Fill(table, Unknown);
        return table;
    }

    /// <summary>
    /// Width of <paramref name="codePoint"/> in cells. Equivalent to
    /// <c>UnicodeCalculator.GetWidth(new Rune(codePoint))</c>.
    /// </summary>
    public static int Get(int codePoint)
    {
        if ((uint)codePoint < BmpEnd)
        {
            var cached = Bmp[codePoint];
            if (cached != Unknown)
                return cached;

            var computed = Compute(codePoint);
            Bmp[codePoint] = computed;
            return computed;
        }

        return UnicodeCalculator.GetWidth(new Rune(codePoint));
    }

    private static sbyte Compute(int codePoint)
    {
        // Rune cannot represent an unpaired surrogate. Callers reach this through EnumerateRunes,
        // which substitutes U+FFFD, so a surrogate should never arrive here — but constructing one
        // would throw, and reporting "not printable" is the safer answer than crashing the parser.
        if (char.IsSurrogate((char)codePoint))
            return -1;

        return (sbyte)UnicodeCalculator.GetWidth(new Rune(codePoint));
    }
}

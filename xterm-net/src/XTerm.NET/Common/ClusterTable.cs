using System.Collections.Concurrent;

namespace XTerm.Common;

/// <summary>
/// Interns the multi-codepoint text of grapheme clusters, so a cell can refer to one by id.
///
/// A cell's text is a single codepoint almost always, and that is derivable from an int. The
/// exceptions — a base character plus combining marks, a ZWJ emoji sequence, a charset mapping that
/// expands to more than one codepoint — need real string storage. Putting that string in the cell
/// would put a GC reference in every cell of the buffer, which is precisely what this exists to
/// avoid: measured, a 240-cell fill costs 238 ns with a reference in the struct and 75 ns without,
/// and a single cell assignment 0.88 ns against 0.35 ns, because the runtime must emit a write
/// barrier for each one.
///
/// Ids are process-wide and stable. Identical cluster text interns to one id, so the table holds one
/// entry per distinct sequence rather than one per cell — a terminal sees a bounded handful of
/// distinct emoji sequences even across a long session.
///
/// Reads are lock-free, which matters because rendering resolves cluster text per frame.
/// </summary>
internal static class ClusterTable
{
    /// <summary>Id 0 means "no cluster"; the cell's codepoint is its whole content.</summary>
    public const int None = 0;

    private static readonly ConcurrentDictionary<int, string> ById = new();
    private static readonly ConcurrentDictionary<string, int> Ids = new(StringComparer.Ordinal);

    private static int _next = None;

    /// <summary>Id for <paramref name="text"/>, allocating one if this is the first time it is seen.</summary>
    public static int Intern(string text)
    {
        if (string.IsNullOrEmpty(text))
            return None;

        if (Ids.TryGetValue(text, out var existing))
            return existing;

        var id = Interlocked.Increment(ref _next);
        ById[id] = text;

        // A race here costs a wasted id, not a wrong answer: whichever writer wins, both ids resolve
        // to equal strings, and the loser's entry is simply never handed out again.
        return Ids.GetOrAdd(text, id);
    }

    /// <summary>Text for <paramref name="id"/>, or empty for <see cref="None"/> or an unknown id.</summary>
    public static string Get(int id) =>
        id != None && ById.TryGetValue(id, out var text) ? text : string.Empty;
}

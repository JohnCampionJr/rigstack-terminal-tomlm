using XTerm;
using XTerm.Graphics;

namespace XTerm.Tests.Graphics;

/// <summary>
/// Asking the buffer what a picture covers, now that pictures live in runs on lines.
/// </summary>
/// <remarks>
/// These replace <c>cell.Image</c> and its companions, which cannot survive the move: a
/// <c>BufferCell</c> is a struct, so a cell copied out of a line has no idea which line or column it
/// came from and cannot answer for a run that is anchored to both. The question is the same one the
/// tests always asked — "is this position showing part of a picture, and which one" — it simply has
/// to be asked of the line.
/// </remarks>
internal static class ImageAssertions
{
    /// <summary>The run covering a screen position, if any.</summary>
    public static LinePlacement? PlacementAt(Terminal terminal, int col, int screenRow)
    {
        var line = terminal.Buffer.Lines[terminal.Buffer.YBase + screenRow];
        if (line is not null && line.TryGetPlacementAt(col, out var placement))
            return placement;

        return null;
    }

    /// <summary>The image shown at a screen position, or null for ordinary text.</summary>
    public static TerminalImage? ImageAt(Terminal terminal, int col, int screenRow)
    {
        var line = terminal.Buffer.Lines[terminal.Buffer.YBase + screenRow];
        return line is not null && line.TryGetImageAt(col, out var image) ? image : null;
    }

    /// <summary>Whether a screen position shows part of a picture.</summary>
    public static bool IsImageAt(Terminal terminal, int col, int screenRow)
        => PlacementAt(terminal, col, screenRow) is not null;

    /// <summary>
    /// How many cells in the whole buffer show part of a picture.
    /// </summary>
    /// <remarks>
    /// Counted from the runs rather than by testing cells, and clamped to each line's width — which
    /// is the point of the model: a run keeps its natural width, so this reports what is VISIBLE at
    /// the current size while nothing wider has been destroyed.
    /// </remarks>
    public static int VisibleImageCells(Terminal terminal)
    {
        var count = 0;

        for (var i = 0; i < terminal.Buffer.Lines.Length; i++)
        {
            var line = terminal.Buffer.Lines[i];
            if (line is null || !line.HasImages)
                continue;

            foreach (var placement in line.Placements)
            {
                var end = System.Math.Min(placement.EndColumn, line.Length);
                count += System.Math.Max(0, end - placement.Column);
            }
        }

        return count;
    }

    /// <summary>
    /// How many cells the buffer's pictures cover at their natural width, visible or not.
    /// </summary>
    /// <remarks>
    /// The difference between this and <see cref="VisibleImageCells"/> is exactly what a narrow
    /// window is hiding rather than destroying — which is the property that used to require hiding
    /// an overhang and reviving it, and now falls out of the storage.
    /// </remarks>
    public static int TotalImageCells(Terminal terminal)
    {
        var count = 0;

        for (var i = 0; i < terminal.Buffer.Lines.Length; i++)
        {
            var line = terminal.Buffer.Lines[i];
            if (line is null || !line.HasImages)
                continue;

            foreach (var placement in line.Placements)
                count += placement.Cols;
        }

        return count;
    }
}

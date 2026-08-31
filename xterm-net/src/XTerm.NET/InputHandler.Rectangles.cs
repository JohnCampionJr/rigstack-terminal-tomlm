using XTerm.Buffer;
using XTerm.Common;
using XTerm.Parser;

namespace XTerm;

/// <summary>
/// The DEC rectangular-area operations: copy, fill and erase (DECCRA, DECFRA, DECERA). One file
/// because they share one coordinate discipline, spelled out on <see cref="TryReadRectangle"/>.
/// </summary>
public partial class InputHandler
{
    /// <summary>
    /// Reads a rectangle from four parameters starting at <paramref name="first"/>, in the
    /// discipline every DEC rectangle operation shares: coordinates are 1-based and inclusive,
    /// interpreted in the ORIGIN MODE coordinate system (a rectangle is addressed the same way a
    /// cursor is), clipped to the screen, and never clipped to the margins -- DECFRA across a
    /// DECSLRM pane fills straight through it, which is what "ignores margins" means in the
    /// standard. Missing values mean the whole screen. A rectangle whose bottom is above its top
    /// or right is left of its left, AFTER origin translation, refuses the whole operation.
    /// </summary>
    /// <returns>False when the operation must do nothing; the bounds are 0-based inclusive.</returns>
    private bool TryReadRectangle(Params parameters, int first,
                                  out int top, out int left, out int bottom, out int right)
    {
        var originX = _terminal.OriginMode ? _buffer.ScrollLeft : 0;
        var originY = _terminal.OriginMode ? _buffer.ScrollTop : 0;

        // An explicit 0 means the default, exactly as an absent parameter does.
        var t = parameters.GetParam(first, 0);
        var l = parameters.GetParam(first + 1, 0);
        var b = parameters.GetParam(first + 2, 0);
        var r = parameters.GetParam(first + 3, 0);

        top = originY + (t <= 0 ? 1 : t) - 1;
        left = originX + (l <= 0 ? 1 : l) - 1;
        bottom = originY + (b <= 0 ? _terminal.Rows - originY : b) - 1;
        right = originX + (r <= 0 ? _terminal.Cols - originX : r) - 1;

        bottom = Math.Min(bottom, _terminal.Rows - 1);
        right = Math.Min(right, _terminal.Cols - 1);

        return top >= 0 && left >= 0 && top <= bottom && left <= right;
    }

    /// <summary>DECFRA -- fills the rectangle with one character, in the CURRENT rendition.</summary>
    /// <remarks>
    /// The character must be printable -- xterm accepts 32..126 and 160 up -- and an
    /// unprintable request refuses the whole operation rather than filling with garbage.
    /// The cursor does not move: a rectangle operation is not a print.
    /// </remarks>
    private void FillRectangularArea(Params parameters)
    {
        var ch = parameters.GetParam(0, 0);
        if (ch < 32 || (ch > 126 && ch < 160))
            return;
        if (!TryReadRectangle(parameters, 1, out var top, out var left, out var bottom, out var right))
            return;

        var cell = new BufferCell(char.ConvertFromUtf32(ch), 1, _curAttr);
        FillCells(top, left, bottom, right, ref cell);
    }

    /// <summary>DECERA -- erases the rectangle to blanks, with the erase attributes.</summary>
    private void EraseRectangularArea(Params parameters)
    {
        if (!TryReadRectangle(parameters, 0, out var top, out var left, out var bottom, out var right))
            return;

        var cell = new BufferCell(" ", 1, GetEraseAttributes());
        FillCells(top, left, bottom, right, ref cell);
    }

    private void FillCells(int top, int left, int bottom, int right, ref BufferCell cell)
    {
        for (var row = top; row <= bottom; row++)
        {
            var line = _buffer.Lines[_buffer.YBase + row];
            if (line is null)
                continue;

            for (var col = left; col <= right && col < line.Length; col++)
                line.SetCell(col, ref cell);
        }
    }

    /// <summary>
    /// DECCRA -- copies a rectangle, cells and attributes together, to a destination named by its
    /// top-left corner.
    /// </summary>
    /// <remarks>
    /// The source is SNAPSHOTTED before a cell is written, which is the whole of what makes an
    /// overlapping copy correct: copying in-place in either direction smears the region across
    /// itself for one of the two overlap orders. The page parameters are accepted and ignored --
    /// there is one page. A destination hanging off the screen edge is clipped, not refused: the
    /// part that fits is the part that copies.
    /// </remarks>
    private void CopyRectangularArea(Params parameters)
    {
        if (!TryReadRectangle(parameters, 0, out var top, out var left, out var bottom, out var right))
            return;

        // parameters[4] is the source page. Destination: top;left (1-based, origin-relative),
        // parameters[7] the destination page.
        var originX = _terminal.OriginMode ? _buffer.ScrollLeft : 0;
        var originY = _terminal.OriginMode ? _buffer.ScrollTop : 0;
        var dt = parameters.GetParam(5, 0);
        var dl = parameters.GetParam(6, 0);
        var destTop = originY + (dt <= 0 ? 1 : dt) - 1;
        var destLeft = originX + (dl <= 0 ? 1 : dl) - 1;

        var rows = bottom - top + 1;
        var cols = right - left + 1;

        var snapshot = new BufferCell[rows, cols];
        for (var row = 0; row < rows; row++)
        {
            var line = _buffer.Lines[_buffer.YBase + top + row];
            for (var col = 0; col < cols; col++)
                snapshot[row, col] = line is not null && left + col < line.Length
                    ? line[left + col]
                    : new BufferCell(" ", 1, AttributeData.Default);
        }

        for (var row = 0; row < rows; row++)
        {
            var destRow = destTop + row;
            if (destRow < 0 || destRow >= _terminal.Rows)
                continue;

            var line = _buffer.Lines[_buffer.YBase + destRow];
            if (line is null)
                continue;

            for (var col = 0; col < cols; col++)
            {
                var destCol = destLeft + col;
                if (destCol < 0 || destCol >= _terminal.Cols || destCol >= line.Length)
                    continue;

                var cell = snapshot[row, col];
                line.SetCell(destCol, ref cell);
            }
        }
    }
}

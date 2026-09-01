using XTerm.Buffer;
using XTerm.Common;
using XTerm.Parser;

namespace XTerm;

/// <summary>
/// The DEC rectangular-area operations: copy, fill, erase and the two that change attributes
/// rather than characters (DECCRA, DECFRA, DECERA, DECSERA, DECCARA, DECRARA). One file because
/// they share one coordinate discipline, spelled out on <see cref="TryReadRectangle"/>.
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

    /// <summary>
    /// DECSERA (CSI Pt;Pl;Pb;Pr $ {). Like DECERA, but DECSCA-protected characters survive.
    /// Only DECSCA counts here: ISO SPA/EPA guards do NOT stop it -- the selective erases and
    /// the guarded erases are separate systems, and this one belongs to DECSCA.
    /// </summary>
    private void SelectiveEraseRectangularArea(Params parameters)
    {
        if (!TryReadRectangle(parameters, 0, out var top, out var left, out var bottom, out var right))
            return;

        var blank = new BufferCell(" ", 1, GetEraseAttributes());
        for (var row = top; row <= bottom; row++)
        {
            var line = _buffer.Lines[_buffer.YBase + row];
            if (line is null)
                continue;

            for (var col = left; col <= right && col < line.Length; col++)
            {
                if (line[col].Attributes.IsProtected())
                    continue;
                line.SetCell(col, ref blank);
            }
        }
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

    /// <summary>
    /// DECCARA (<c>CSI Pt;Pl;Pb;Pr;Pm $ r</c>) and DECRARA (<c>CSI Pt;Pl;Pb;Pr;Pm $ t</c>) -- set
    /// or toggle the named SGR attributes over an area, leaving the characters alone.
    /// </summary>
    /// <remarks>
    /// <para>The attribute half of the rectangle family, and the only consumer DECSACE has. That
    /// setting was parsed, stored and read back by DECRQSS while nothing acted on it, because the
    /// two controls it governs did not exist: a terminal reporting a rectangle-or-stream choice it
    /// then ignored.</para>
    /// <para>DECSACE 2 means the RECTANGLE the four coordinates describe. Anything else -- the
    /// default included -- means the STREAM running from the top-left position to the bottom-right
    /// one, so the first row runs from its column to the end of the line, the last row from the
    /// start of the line to its column, and every row between them runs whole.</para>
    /// <para>Only the six attributes DEC defines are touched: 1 bold, 4 underline, 5 blink,
    /// 7 inverse and their resets 22, 24, 25, 27, plus xterm's 8/28 for invisible. Parameter 0
    /// means the first four together -- NOT invisible, which xterm leaves out of its SGR_MASK --
    /// and reverses rather than clears them under DECRARA. Everything else in the list is ignored;
    /// colours are not in the standard, and honouring an SGR parameter here that a real VT420 would
    /// not is how a program's careful rectangle ends up recoloured on one terminal only.</para>
    /// <para>Every cell in the area is marked, the trailing half of a wide character included. xterm
    /// skips cells it has never drawn -- it tracks that per cell, and a blank it has never touched
    /// is not a blank it will colour -- but a line here is born full of spaces, so there is no such
    /// state to test for and the only cell that reads as empty is a wide character's second half.
    /// Skipping THAT would leave a character's two halves disagreeing about their own rendition.</para>
    /// </remarks>
    private void MarkRectangularArea(Params parameters, bool reverse)
    {
        if (!TryReadRectangle(parameters, 0, out var top, out var left, out var bottom, out var right))
            return;

        var exact = _attributeChangeExtent == 2;

        for (var row = top; row <= bottom; row++)
        {
            var line = _buffer.Lines[_buffer.YBase + row];
            if (line is null)
                continue;

            var from = exact || row == top ? left : 0;
            var to = exact || row == bottom ? right : _terminal.Cols - 1;

            for (var col = from; col <= to && col < line.Length; col++)
            {
                var cell = line[col];
                ApplyAreaAttributes(parameters, 4, ref cell.Attributes, reverse);
                line.SetCell(col, ref cell);
            }
        }
    }

    /// <summary>
    /// Applies the DECCARA/DECRARA attribute list starting at <paramref name="first"/> to one
    /// cell's rendition.
    /// </summary>
    private static void ApplyAreaAttributes(Params parameters, int first, ref AttributeData attributes, bool reverse)
    {
        for (var i = first; i < parameters.Length; i++)
        {
            switch (parameters.GetParam(i, 0))
            {
                case 0:
                    if (reverse)
                    {
                        attributes.SetBold(!attributes.IsBold());
                        attributes.SetUnderline(!attributes.IsUnderline());
                        attributes.SetBlink(!attributes.IsBlink());
                        attributes.SetInverse(!attributes.IsInverse());
                    }
                    else
                    {
                        attributes.SetBold(false);
                        attributes.SetUnderline(false);
                        attributes.SetBlink(false);
                        attributes.SetInverse(false);
                    }
                    break;
                case 1:
                    attributes.SetBold(reverse ? !attributes.IsBold() : true);
                    break;
                case 4:
                    attributes.SetUnderline(reverse ? !attributes.IsUnderline() : true);
                    break;
                case 5:
                    attributes.SetBlink(reverse ? !attributes.IsBlink() : true);
                    break;
                case 7:
                    attributes.SetInverse(reverse ? !attributes.IsInverse() : true);
                    break;
                case 8:
                    attributes.SetInvisible(reverse ? !attributes.IsInvisible() : true);
                    break;
                // The resets have no meaning under DECRARA -- reversing an attribute already says
                // both directions -- so xterm reads them only when setting, and so does this.
                case 22 when !reverse:
                    attributes.SetBold(false);
                    break;
                case 24 when !reverse:
                    attributes.SetUnderline(false);
                    break;
                case 25 when !reverse:
                    attributes.SetBlink(false);
                    break;
                case 27 when !reverse:
                    attributes.SetInverse(false);
                    break;
                case 28 when !reverse:
                    attributes.SetInvisible(false);
                    break;
            }
        }
    }
}

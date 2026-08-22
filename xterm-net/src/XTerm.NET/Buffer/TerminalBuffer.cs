using System.Text;
using XTerm.Common;

namespace XTerm.Buffer;

/// <summary>
/// Main terminal buffer that manages the active screen and scrollback.
/// </summary>
public class TerminalBuffer
{
    private readonly CircularList<BufferLine> _lines;
    private readonly bool _hasScrollback;
    private int _yDisp;
    private int _yBase;
    private int _y;
    private int _x;
    private int _scrollBottom;
    private int _scrollTop;
    private int _cols;
    private int _rows;

    /// <summary>
    /// The absolute line index of the top of the viewport in the buffer.
    /// In XTerm.js this is 'ydisp'. This represents the current scroll position.
    /// </summary>
    public int ViewportY
    {
        get => _yDisp;
        set => _yDisp = Math.Clamp(value, 0, _yBase);
    }

    /// <summary>
    /// The absolute line index where new content is being written.
    /// In XTerm.js this is 'ybase'. This represents the bottom of the active content.
    /// </summary>
    public int BaseY => _yBase;

    /// <summary>
    /// Total number of lines in the buffer (scrollback + active lines).
    /// </summary>
    public int Length => _lines.Length;

    /// <summary>
    /// Whether the viewport is at the bottom (showing latest content).
    /// In xterm.js: ydisp === ybase means we're at the bottom.
    /// </summary>
    public bool IsAtBottom => _yDisp >= _yBase;

    /// <summary>
    /// Number of columns in the buffer.
    /// </summary>
    public int Cols => _cols;

    /// <summary>
    /// Number of rows in the buffer (viewport height).
    /// </summary>
    public int Rows => _rows;

    // Legacy properties for backward compatibility
    public int YDisp => _yDisp;
    public int YBase => _yBase;
    public int Y => _y;
    public int X => _x;
    public int ScrollTop => _scrollTop;
    public int ScrollBottom => _scrollBottom;

    public CircularList<BufferLine> Lines => _lines;

    /// <summary>
    /// Fired when lines are trimmed from the start of the buffer.
    /// </summary>
    public event Action<int>? Trimmed;

    /// <summary>
    /// Saved cursor state for DECSC/DECRC.
    /// </summary>
    public class SavedCursor
    {
        public int X { get; set; }
        public int Y { get; set; }
        public AttributeData Attr { get; set; }
        public CharsetMode Charset { get; set; }

        public SavedCursor()
        {
            X = 0;
            Y = 0;
            Attr = AttributeData.Default;
            Charset = CharsetMode.G0;
        }
    }

    public SavedCursor SavedCursorState { get; set; }

    public TerminalBuffer(int cols, int rows, int scrollback, bool hasScrollback = true)
    {
        _hasScrollback = hasScrollback;
        _cols = cols;
        _rows = rows;
        _lines = new CircularList<BufferLine>(rows + scrollback);
        _yDisp = 0;
        _yBase = 0;
        _y = 0;
        _x = 0;
        _scrollTop = 0;
        _scrollBottom = rows - 1;
        SavedCursorState = new SavedCursor();

        // Initialize buffer with empty lines
        for (int i = 0; i < rows; i++)
        {
            _lines.Push(new BufferLine(cols, BufferCell.Space));
        }
    }

    private bool IsReflowEnabled => _hasScrollback && _lines.MaxLength > _rows;

    /// <summary>
    /// Gets a line from the buffer.
    /// </summary>
    public BufferLine? GetLine(int y)
    {
        return _lines[y];
    }

    /// <summary>
    /// Gets a blank line (filled with null cells).
    /// </summary>
    public BufferLine GetBlankLine(AttributeData attr, bool isWrapped = false)
    {
        var fillCell = BufferCell.Space;
        fillCell.Attributes = attr;
        return new BufferLine(_cols, fillCell) { IsWrapped = isWrapped };
    }

    /// <summary>
    /// Scrolls the buffer up by a specified number of lines.
    /// This matches xterm.js Buffer.scroll() behavior.
    /// </summary>
    public void ScrollUp(int lines, bool isWrapped = false)
    {
        for (int i = 0; i < lines; i++)
        {
            // Create a new blank line that will be inserted at the bottom of the scroll region
            var newLine = GetBlankLine(AttributeData.Default, isWrapped);

            // Only the full-screen scroll region contributes to scrollback.
            // Top-anchored partial regions reserve rows below the margin and
            // must scroll in place so prompts/status rows are not promoted.
            if (_scrollTop == 0 && _scrollBottom == _rows - 1 && _lines.MaxLength > _rows)
            {
                // When scrollTop is 0, the top line goes into scrollback.
                // In xterm.js: push new line first, then increment yBase and yDisp.
                // This causes the circular list to potentially recycle the oldest line.

                // Check if we're at max capacity - if so, yBase stays the same but 
                // the buffer rotates. If not, yBase increments.
                var willBeRecycled = _lines.Length >= _lines.MaxLength;

                // Push the new line at the end (bottom of screen in buffer terms)
                _lines.Push(newLine);

                if (willBeRecycled)
                {
                    Trimmed?.Invoke(1);
                }

                // Only increment yBase if the buffer didn't recycle
                if (!willBeRecycled)
                {
                    _yBase++;
                }

                // If yDisp was at the bottom, keep it there
                if (_yDisp + 1 < _yBase)
                {
                    // User was scrolled up, don't auto-scroll
                }
                else
                {
                    _yDisp = _yBase;
                }
            }
            else
            {
                // Scroll region is not at top of screen.
                // Remove line from scroll region top and add blank at bottom.
                // Use yBase offset for correct absolute positioning.
                var scrollRegionStart = _yBase + _scrollTop;
                var scrollRegionEnd = _yBase + _scrollBottom;

                // Delete the line at the top of scroll region
                _lines.Splice(scrollRegionStart, 1);

                // Insert blank line at bottom of scroll region
                _lines.Splice(scrollRegionEnd, 0, newLine);
            }
        }
    }

    /// <summary>
    /// Scrolls the buffer down by a specified number of lines.
    /// This is reverse scrolling within the scroll region.
    /// </summary>
    public void ScrollDown(int lines)
    {
        for (int i = 0; i < lines; i++)
        {
            // Calculate absolute positions in the buffer
            var scrollRegionStart = _yBase + _scrollTop;
            var scrollRegionEnd = _yBase + _scrollBottom;

            // Remove line from scroll region bottom
            _lines.Splice(scrollRegionEnd, 1);

            // Add blank line at top of scroll region
            var newLine = GetBlankLine(AttributeData.Default);
            _lines.Splice(scrollRegionStart, 0, newLine);
        }
    }

    /// <summary>
    /// Scrolls the display by a specified amount.
    /// This only changes the viewport position, not the buffer content.
    /// </summary>
    public void ScrollDisp(int disp, bool suppressScrollEvent = false)
    {
        _yDisp = Math.Clamp(_yDisp + disp, 0, _yBase);
    }

    /// <summary>
    /// Scrolls the viewport to show a specific line.
    /// </summary>
    /// <param name="line">The absolute line number to scroll to</param>
    public void ScrollToLine(int line)
    {
        _yDisp = Math.Clamp(line, 0, _yBase);
    }

    /// <summary>
    /// Scrolls the display to the bottom (showing active screen).
    /// In xterm.js, yDisp = yBase means showing the active terminal area.
    /// </summary>
    public void ScrollToBottom()
    {
        _yDisp = _yBase;
    }

    /// <summary>
    /// Scrolls the display to the top.
    /// </summary>
    public void ScrollToTop()
    {
        _yDisp = 0;
    }

    /// <summary>
    /// Discards the scrollback — every line above the visible screen — leaving the visible screen and the
    /// cursor untouched.
    /// </summary>
    /// <remarks>
    /// <para>This is what <c>CSI 3 J</c> asks for, and it is a different operation from erasing: the lines
    /// are REMOVED from the buffer rather than blanked, so the history is genuinely gone and cannot be
    /// scrolled back to.</para>
    /// <para><c>_yBase</c> and <c>_yDisp</c> must move with the lines. They are absolute indices into the
    /// buffer, so trimming from the start without adjusting them leaves the visible screen indexed at an
    /// offset that no longer exists, and the next write runs off the end of the list.</para>
    /// </remarks>
    public void ClearScrollback()
    {
        if (_yBase == 0)
            return;

        _lines.TrimStart(_yBase);
        _yBase = 0;
        _yDisp = 0;
    }

    /// <summary>
    /// Scrolls the viewport by a relative number of lines.
    /// </summary>
    /// <param name="lines">Number of lines to scroll (negative = up, positive = down)</param>
    public void ScrollLines(int lines)
    {
        ScrollToLine(_yDisp + lines);
    }

    /// <summary>
    /// Sets the scroll region.
    /// </summary>
    public void SetScrollRegion(int top, int bottom)
    {
        _scrollTop = Math.Clamp(top, 0, _rows - 1);
        _scrollBottom = Math.Clamp(bottom, _scrollTop, _rows - 1);
    }

    /// <summary>
    /// Resets the scroll region to full screen.
    /// </summary>
    public void ResetScrollRegion()
    {
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
    }

    /// <summary>
    /// Gets the absolute line index for a viewport-relative y coordinate.
    /// </summary>
    public int GetAbsoluteY(int y)
    {
        return _yBase + y;
    }

    /// <summary>
    /// Resizes the buffer.
    /// </summary>
    public void Resize(int newCols, int newRows)
    {
        var nullCell = BufferCell.Space;
        var newMaxLength = newRows + (_lines.MaxLength - _rows);

        if (newMaxLength > _lines.MaxLength)
        {
            _lines.Resize(newMaxLength);
        }

        if (_lines.Length > 0)
        {
            if (_cols < newCols)
            {
                for (int i = 0; i < _lines.Length; i++)
                {
                    _lines[i]?.Resize(newCols, nullCell);
                }
            }

            while (_lines.Length < newRows)
            {
                _lines.Push(new BufferLine(newCols, nullCell));
            }

            _yBase = Math.Min(_yBase, Math.Max(0, _lines.Length - newRows));
            _yDisp = Math.Clamp(_yDisp, 0, _yBase);

            if (IsReflowEnabled && newCols != _cols)
            {
                if (newCols > _cols)
                {
                    ReflowLarger(newCols, newRows);
                }
                else
                {
                    ReflowSmaller(newCols, newRows);
                }
            }

            if (_cols > newCols)
            {
                for (int i = 0; i < _lines.Length; i++)
                {
                    _lines[i]?.Resize(newCols, nullCell);
                }
            }
        }

        var oldRows = _rows;
        _cols = newCols;
        _rows = newRows;

        if (_scrollBottom == oldRows - 1)
        {
            _scrollBottom = newRows - 1;
        }
        else
        {
            _scrollBottom = Math.Min(_scrollBottom, newRows - 1);
        }
        _scrollTop = Math.Min(_scrollTop, newRows - 1);

        _x = Math.Min(_x, newCols - 1);
        _y = Math.Min(_y, newRows - 1);
        SavedCursorState.X = Math.Min(SavedCursorState.X, newCols - 1);

        if (newMaxLength < _lines.MaxLength)
        {
            var amountToTrim = _lines.Length - newMaxLength;
            if (amountToTrim > 0)
            {
                _lines.TrimStart(amountToTrim);
                Trimmed?.Invoke(amountToTrim);
                _yBase = Math.Max(_yBase - amountToTrim, 0);
                _yDisp = Math.Max(_yDisp - amountToTrim, 0);
                SavedCursorState.Y = Math.Max(SavedCursorState.Y - amountToTrim, 0);
            }
            _lines.Resize(newMaxLength);
        }
    }

    private void ReflowLarger(int newCols, int newRows)
    {
        var nullCell = BufferCell.Space;
        var toRemove = BufferReflow.ReflowLargerGetLinesToRemove(
            _lines, _cols, newCols, _yBase + _y, nullCell);
        if (toRemove.Length > 0)
        {
            var newLayoutResult = BufferReflow.ReflowLargerCreateNewLayout(_lines, toRemove);
            BufferReflow.ReflowLargerApplyNewLayout(_lines, newLayoutResult.Layout);
            ReflowLargerAdjustViewport(newCols, newRows, newLayoutResult.CountRemoved);
        }
    }

    private void ReflowLargerAdjustViewport(int newCols, int newRows, int countRemoved)
    {
        var nullCell = BufferCell.Space;
        var viewportAdjustments = countRemoved;
        while (viewportAdjustments-- > 0)
        {
            if (_yBase == 0)
            {
                if (_y > 0)
                {
                    _y--;
                }
                if (_lines.Length < newRows)
                {
                    _lines.Push(new BufferLine(newCols, nullCell));
                }
            }
            else
            {
                if (_yDisp == _yBase)
                {
                    _yDisp--;
                }
                _yBase--;
            }
        }
        SavedCursorState.Y = Math.Max(SavedCursorState.Y - countRemoved, 0);
    }

    private void ReflowSmaller(int newCols, int newRows)
    {
        var nullCell = BufferCell.Space;
        var toInsert = new List<(int Start, List<BufferLine> NewLines)>();
        var countToInsert = 0;

        for (var y = _lines.Length - 1; y >= 0; y--)
        {
            var nextLine = _lines[y];
            if (nextLine == null || (!nextLine.IsWrapped && nextLine.GetTrimmedLength() <= newCols))
            {
                continue;
            }

            var wrappedLines = new List<BufferLine> { nextLine };
            while (nextLine.IsWrapped && y > 0)
            {
                nextLine = _lines[--y]!;
                wrappedLines.Insert(0, nextLine);
            }

            if (BufferReflow.HasNonNormalLineAttribute(wrappedLines))
            {
                continue;
            }

            var absoluteY = _yBase + _y;
            if (absoluteY >= y && absoluteY < y + wrappedLines.Count)
            {
                continue;
            }

            var lastLineLength = wrappedLines[^1].GetTrimmedLength();
            var destLineLengths = BufferReflow.ReflowSmallerGetNewLineLengths(wrappedLines, _cols, newCols);
            var linesToAdd = destLineLengths.Length - wrappedLines.Count;
            int trimmedLines;
            if (_yBase == 0 && _y != _lines.Length - 1)
            {
                trimmedLines = Math.Max(0, _y - _lines.MaxLength + linesToAdd);
            }
            else
            {
                trimmedLines = Math.Max(0, _lines.Length - _lines.MaxLength + linesToAdd);
            }

            var newLines = new List<BufferLine>();
            for (var i = 0; i < linesToAdd; i++)
            {
                newLines.Add(GetBlankLine(AttributeData.Default, isWrapped: true));
            }

            if (newLines.Count > 0)
            {
                toInsert.Add((y + wrappedLines.Count + countToInsert, newLines));
                countToInsert += newLines.Count;
            }

            wrappedLines.AddRange(newLines);

            var destLineIndex = destLineLengths.Length - 1;
            var destCol = destLineLengths[destLineIndex];
            if (destCol == 0)
            {
                destLineIndex--;
                destCol = destLineLengths[destLineIndex];
            }

            var srcLineIndex = wrappedLines.Count - linesToAdd - 1;
            var srcCol = lastLineLength;
            while (srcLineIndex >= 0)
            {
                var cellsToCopy = Math.Min(srcCol, destCol);
                if (wrappedLines[destLineIndex] == null)
                {
                    break;
                }

                wrappedLines[destLineIndex].CopyCellsFrom(
                    wrappedLines[srcLineIndex], srcCol - cellsToCopy, destCol - cellsToCopy, cellsToCopy, true);
                destCol -= cellsToCopy;
                if (destCol == 0)
                {
                    destLineIndex--;
                    if (destLineIndex < 0)
                    {
                        break;
                    }
                    destCol = destLineLengths[destLineIndex];
                }
                srcCol -= cellsToCopy;
                if (srcCol == 0)
                {
                    srcLineIndex--;
                    var wrappedLinesIndex = Math.Max(srcLineIndex, 0);
                    srcCol = BufferReflow.GetWrappedLineTrimmedLength(wrappedLines, wrappedLinesIndex, _cols);
                }
            }

            for (var i = 0; i < wrappedLines.Count && i < destLineLengths.Length; i++)
            {
                if (destLineLengths[i] < newCols)
                {
                    wrappedLines[i].ReplaceCells(destLineLengths[i], newCols, nullCell);
                }
            }

            var viewportAdjustments = linesToAdd - trimmedLines;
            while (viewportAdjustments-- > 0)
            {
                if (_yBase == 0)
                {
                    if (_y < newRows - 1)
                    {
                        _y++;
                        _lines.Pop();
                    }
                    else
                    {
                        _yBase++;
                        _yDisp++;
                    }
                }
                else
                {
                    if (_yBase < Math.Min(_lines.MaxLength, _lines.Length + countToInsert) - newRows)
                    {
                        if (_yBase == _yDisp)
                        {
                            _yDisp++;
                        }
                        _yBase++;
                    }
                }
            }

            SavedCursorState.Y = Math.Min(SavedCursorState.Y + linesToAdd, _yBase + newRows - 1);
        }

        if (toInsert.Count > 0)
        {
            var originalLines = new List<BufferLine>(_lines.Length);
            for (var i = 0; i < _lines.Length; i++)
            {
                originalLines.Add(_lines[i]!);
            }

            var originalLinesLength = originalLines.Count;
            RebuildWithInsertions(originalLines, toInsert, countToInsert);

            var amountToTrim = Math.Max(0, originalLinesLength + countToInsert - _lines.MaxLength);
            if (amountToTrim > 0)
            {
                _yBase = Math.Max(_yBase - amountToTrim, 0);
                _yDisp = Math.Max(_yDisp - amountToTrim, 0);
                SavedCursorState.Y = Math.Max(SavedCursorState.Y - amountToTrim, 0);
                Trimmed?.Invoke(amountToTrim);
            }
        }
    }

    private void RebuildWithInsertions(
        IReadOnlyList<BufferLine> originalLines,
        IReadOnlyList<(int Start, List<BufferLine> NewLines)> toInsert,
        int countInserted)
    {
        var originalLinesLength = originalLines.Count;
        _lines.SetLength(Math.Min(_lines.MaxLength, originalLinesLength + countInserted));

        var originalLineIndex = originalLinesLength - 1;
        var nextToInsertIndex = 0;
        var nextToInsert = nextToInsertIndex < toInsert.Count ? toInsert[nextToInsertIndex] : ((int Start, List<BufferLine> NewLines)?)null;
        var countInsertedSoFar = 0;

        for (var i = Math.Min(_lines.MaxLength - 1, originalLinesLength + countInserted - 1); i >= 0; i--)
        {
            if (nextToInsert.HasValue && nextToInsert.Value.Start > originalLineIndex + countInsertedSoFar)
            {
                var insert = nextToInsert.Value;
                for (var nextI = insert.NewLines.Count - 1; nextI >= 0; nextI--)
                {
                    _lines[i--] = insert.NewLines[nextI];
                }
                i++;

                countInsertedSoFar += insert.NewLines.Count;
                nextToInsertIndex++;
                nextToInsert = nextToInsertIndex < toInsert.Count ? toInsert[nextToInsertIndex] : null;
            }
            else
            {
                _lines[i] = originalLines[originalLineIndex--];
            }
        }
    }

    /// <summary>
    /// Sets the cursor position.
    /// </summary>
    public void SetCursor(int x, int y)
    {
        _x = Math.Clamp(x, 0, _cols - 1);
        _y = Math.Clamp(y, 0, _rows - 1);
    }

    /// <summary>
    /// Moves the cursor to the specified position without any clamping.
    /// </summary>
    public void SetCursorRaw(int x, int y)
    {
        _x = x;
        _y = y;
    }

    public string PrintViewport()
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < _rows; i++)
        {
            var line = GetLine(_yDisp + i);
            if (line != null)
            {
                foreach (var cell in line)
                {
                    sb.Append(cell.Content);
                }
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

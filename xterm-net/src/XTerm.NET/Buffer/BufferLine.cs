using System.Collections;
using System.Text;

namespace XTerm.Buffer;

/// <summary>
/// Represents a single line in the terminal buffer.
/// Contains an array of cells and metadata about the line.
/// </summary>
public class BufferLine : IEnumerable<BufferCell>
{
    private BufferCell[] _cells;

    /// <summary>
    /// The picture runs shown on this line, or null — which is every line, in almost every session.
    /// </summary>
    /// <remarks>
    /// This is where a picture LIVES. Cells carry no image data at all, so nothing about a picture
    /// is destroyed by anything that truncates or overwrites cells, and a resize needs to do nothing
    /// to images whatsoever: the renderer draws as much of each run as the current width allows.
    /// </remarks>
    private List<Graphics.LinePlacement>? _placements;

    /// <summary>
    /// The images those runs refer to, held strongly so they stay alive exactly as long as this line
    /// does — so a picture scrolled off the end of the scrollback dies with the last line showing it,
    /// with no eviction pass and nothing to keep in step with a buffer that scrolls.
    /// </summary>
    private List<Graphics.TerminalImage>? _images;
    private int _length;
    private bool _isWrapped;
    private LineAttribute _lineAttribute;

    public int Length => _length;

    public bool IsWrapped
    {
        get => _isWrapped;
        set => _isWrapped = value;
    }

    /// <summary>
    /// Gets or sets the DEC line attribute (double-width/double-height).
    /// Set via ESC # sequences: ESC # 3 (top), ESC # 4 (bottom), ESC # 5 (normal), ESC # 6 (double-width).
    /// </summary>
    public LineAttribute LineAttribute
    {
        get => _lineAttribute;
        set
        {
            _lineAttribute = value;
            Cache = null;
        }
    }

    /// <summary>
    /// Returns true if this line has a double-width attribute (DECDWL or DECDHL).
    /// Double-width lines can only display cols/2 characters.
    /// </summary>
    public bool IsDoubleWidth => _lineAttribute.IsDoubleWidth();

    /// <summary>
    /// Cache object - this will be cleared on writes to the bufferline.
    /// </summary>
    public object? Cache { get; set; }

    public BufferLine(int cols, BufferCell? fillCell = null)
    {
        _length = cols;
        _cells = new BufferCell[cols];
        _isWrapped = false;
        _lineAttribute = LineAttribute.Normal;

        var fill = fillCell ?? BufferCell.Space;
        for (int i = 0; i < cols; i++)
        {
            _cells[i] = fill;
        }
        Cache = null;
    }

    /// <summary>
    /// Gets or sets a cell at a specific column.
    /// </summary>
    public BufferCell this[int index]
    {
        get
        {
            if (index < 0 || index >= _length)
                return BufferCell.Empty;
            return _cells[index];
        }
        set
        {
            if (index >= 0 && index < _length)
            {
                _cells[index] = value;
                Cache = null;
            }
        }
    }

    /// <summary>
    /// Sets a cell at a specific column.
    /// </summary>
    public void SetCell(int index, ref BufferCell cell)
    {
        if (index >= 0 && index < _length)
        {
            _cells[index] = cell;

            // Printing over a Sixel picture replaces that part of it. With tiles in cells this
            // happened for free; with runs it is explicit. One field test on the overwhelmingly
            // common line, which has no pictures at all.
            if (_placements is not null)
                SplitPlacementsAt(index);

            Cache = null;
        }
    }

    /// <summary>
    /// Gets the cell code point at a specific column.
    /// </summary>
    public int GetCodePoint(int index)
    {
        if (index >= 0 && index < _length)
            return _cells[index].CodePoint;
        return 0;
    }

    /// <summary>
    /// Resizes the line to a new column count.
    /// </summary>
    public void Resize(int cols, BufferCell fillCell)
    {
        if (cols == _length)
            return;

        if (cols > _length)
        {
            var newCells = new BufferCell[cols];
            Array.Copy(_cells, newCells, _length);
            for (int i = _length; i < cols; i++)
            {
                newCells[i] = fillCell;
            }
            _cells = newCells;
        }
        else
        {
            var newCells = new BufferCell[cols];
            Array.Copy(_cells, newCells, cols);
            _cells = newCells;
        }
        Cache = null;
        _length = cols;
    }

    /// <summary>
    /// Fills a range of cells with a specific cell.
    /// </summary>
    public void Fill(BufferCell fillCell, int startCol = 0, int endCol = -1)
    {
        if (endCol == -1)
            endCol = _length;

        for (int i = startCol; i < endCol && i < _length; i++)
        {
            _cells[i] = fillCell;
        }

        // Erasing takes any picture in the span with it, the same as printing over one does.
        if (_placements is not null)
            SplitPlacementsOver(startCol, Math.Max(0, Math.Min(endCol, _length) - startCol));

        Cache = null;
    }

    /// <summary>
    /// Copies cells from another line.
    /// </summary>
    public void CopyCellsFrom(BufferLine src, int srcCol, int destCol, int length, bool applyInReverse)
    {
        if (applyInReverse)
        {
            for (int i = length - 1; i >= 0; i--)
            {
                if (destCol + i < _length && srcCol + i < src._length)
                {
                    _cells[destCol + i] = src._cells[srcCol + i];
                }
            }
        }
        else
        {
            for (int i = 0; i < length; i++)
            {
                if (destCol + i < _length && srcCol + i < src._length)
                {
                    _cells[destCol + i] = src._cells[srcCol + i];
                }
            }
        }
        Cache = null;
    }

    /// <summary>
    /// Translates the line to a string.
    /// </summary>
    public string TranslateToString(bool trimRight = false, int startCol = 0, int endCol = -1)
    {
        if (endCol == -1)
            endCol = _length;

        var sb = new StringBuilder();
        for (int i = startCol; i < endCol && i < _length; i++)
        {
            var cell = _cells[i];
            sb.Append(cell.Content);
        }

        if (trimRight)
        {
            return sb.ToString().TrimEnd();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Gets the width of the cell at the given column.
    /// </summary>
    public int GetWidth(int index)
    {
        if (index < 0 || index >= _length)
            return 1;
        return _cells[index].Width;
    }

    /// <summary>
    /// Returns whether the cell at the given column has content.
    /// </summary>
    public bool HasContent(int index)
    {
        if (index < 0 || index >= _length)
            return false;
        var cell = _cells[index];
        return !cell.IsSpace() && !cell.IsEmpty();
    }

    /// <summary>
    /// Replaces cells in the range [startCol, endCol) with the fill cell.
    /// </summary>
    public void ReplaceCells(int startCol, int endCol, BufferCell fillCell)
    {
        if (startCol > 0 && GetWidth(startCol - 1) == 2)
        {
            _cells[startCol - 1] = fillCell;
        }
        if (endCol < _length && GetWidth(endCol - 1) == 2)
        {
            _cells[endCol] = fillCell;
        }
        while (startCol < endCol && startCol < _length)
        {
            _cells[startCol++] = fillCell;
        }
        Cache = null;
    }

    /// <summary>
    /// Drops any image pieces on this line, leaving the cells blank but otherwise untouched.
    /// </summary>
    /// <returns>True if the line held any, which is also the signal that it needs repainting.</returns>
    public bool ClearImages()
    {
        if (_placements is null && _images is null)
            return false;

        // Nothing to clean up in the cells — they never held anything. Releasing the strong
        // references is what actually frees the pixels.
        _placements = null;
        _images = null;
        Cache = null;
        return true;
    }

    /// <summary>
    /// Whether this line shows any part of a picture. One field test — a renderer can ask per row.
    /// </summary>
    public bool HasImages => _placements is { Count: > 0 };

    /// <summary>The picture runs on this line, in the order they were placed.</summary>
    public IReadOnlyList<Graphics.LinePlacement> Placements
        => (IReadOnlyList<Graphics.LinePlacement>?)_placements ?? Array.Empty<Graphics.LinePlacement>();

    /// <summary>
    /// The run covering <paramref name="column"/>, if any.
    /// </summary>
    /// <remarks>
    /// This is what replaces asking a CELL about its image. A cell is a struct with no idea which
    /// line or column it came from, so it cannot answer for a run anchored to both — the question
    /// can only be asked here. Linear over the runs, of which a line has one or a handful.
    /// </remarks>
    public bool TryGetPlacementAt(int column, out Graphics.LinePlacement placement)
    {
        if (_placements is not null)
        {
            for (int i = 0; i < _placements.Count; i++)
            {
                if (_placements[i].Covers(column))
                {
                    placement = _placements[i];
                    return true;
                }
            }
        }

        placement = default;
        return false;
    }

    /// <summary>
    /// The image shown at <paramref name="column"/>, if any.
    /// </summary>
    /// <remarks>
    /// Resolved from the line's own strong references, so a caller gets the picture without knowing
    /// that ids exist and without touching a weak table it might race.
    /// </remarks>
    public bool TryGetImageAt(int column, out Graphics.TerminalImage image)
    {
        if (TryGetPlacementAt(column, out var placement) && _images is not null)
        {
            foreach (var held in _images)
            {
                if (held.Id == placement.ImageId)
                {
                    image = held;
                    return true;
                }
            }
        }

        image = null!;
        return false;
    }

    /// <summary>Adds a run to this line and takes ownership of the image it shows.</summary>
    internal void AddPlacement(Graphics.LinePlacement placement, Graphics.TerminalImage image)
    {
        if (placement.Cols <= 0)
            return;

        _placements ??= new List<Graphics.LinePlacement>(1);
        _placements.Add(placement);

        _images ??= new List<Graphics.TerminalImage>(1);
        foreach (var held in _images)
        {
            if (ReferenceEquals(held, image))
            {
                Cache = null;
                return;
            }
        }

        _images.Add(image);
        Cache = null;
    }

    /// <summary>
    /// Splits any Sixel run covering <paramref name="column"/> around the text just written there.
    /// </summary>
    /// <remarks>
    /// <para>Sixel semantics: printing replaces that part of the picture. With tiles in cells this
    /// happened for free, because the write overwrote the cell; with runs it has to be done on
    /// purpose. The run becomes the fragments either side, each with its source rectangle narrowed
    /// to match, so the rest of the picture survives a character landing in the middle of it.</para>
    /// <para>Kitty runs are left alone — there the z-index decides what is on top, and text never
    /// modifies a placement.</para>
    /// <para>Guarded on a null field at every call site, so a line without pictures — which is
    /// nearly every line — pays a single test.</para>
    /// </remarks>
    internal void SplitPlacementsAt(int column)
    {
        if (_placements is null)
            return;

        for (int i = _placements.Count - 1; i >= 0; i--)
        {
            var placement = _placements[i];
            if (placement.Kind != Graphics.PlacementKind.Sixel || !placement.Covers(column))
                continue;

            _placements.RemoveAt(i);

            var before = placement.TruncatedBefore(column);
            if (before.Cols > 0)
                _placements.Insert(i, before);

            var after = placement.TruncatedAfter(column);
            if (after.Cols > 0)
                _placements.Insert(before.Cols > 0 ? i + 1 : i, after);
        }

        if (_placements.Count == 0)
        {
            _placements = null;
            _images = null;
        }
    }

    /// <summary>Splits runs across a whole written span.</summary>
    internal void SplitPlacementsOver(int column, int count)
    {
        if (_placements is null)
            return;

        for (int i = 0; i < count; i++)
            SplitPlacementsAt(column + i);
    }

    /// <summary>
    /// Gets the last non-whitespace cell index.
    /// </summary>
    public int GetTrimmedLength()
    {
        for (int i = _length - 1; i >= 0; i--)
        {
            if (!_cells[i].IsSpace() && !_cells[i].IsEmpty())
                return i + Math.Max(_cells[i].Width, 1);
        }
        return 0;
    }

    /// <summary>
    /// Clones the line.
    /// </summary>
    public BufferLine Clone()
    {
        var newLine = new BufferLine(_length);
        newLine._isWrapped = _isWrapped;
        newLine._lineAttribute = _lineAttribute;

        // The runs are the picture, so a clone that skipped them would silently lose it.
        if (_placements is not null)
        {
            newLine._placements = new List<Graphics.LinePlacement>(_placements);
            newLine._images = _images is null ? null : new List<Graphics.TerminalImage>(_images);
        }
        for (int i = 0; i < _length; i++)
        {
            newLine._cells[i] = _cells[i];
        }
        newLine.Cache = this.Cache;
        return newLine;
    }

    /// <summary>
    /// Copies the line into another line.
    /// </summary>
    public void CopyFrom(BufferLine line)
    {
        if (_length != line._length)
        {
            _cells = new BufferCell[line._length];
            _length = line._length;
        }

        for (int i = 0; i < _length; i++)
        {
            _cells[i] = line._cells[i];
        }
        _isWrapped = line._isWrapped;
        _lineAttribute = line._lineAttribute;
        this.Cache = line.Cache;
    }

    public IEnumerator<BufferCell> GetEnumerator()
    {
        return _cells.AsEnumerable().GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return _cells.GetEnumerator();
    }
}
